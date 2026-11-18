"""FleetPulse fleet simulator.

Generates candump-format J1939 logs for a fleet of trucks, one file per truck
per operating day, plus a ground-truth file describing the injected faults.
The ground truth is only used to *evaluate* the analytics; the API and the
model never read it.

What is simulated
-----------------
* Daily trips (Sundays off) sampled every 30 s: vehicle speed, engine speed,
  coolant temperature, fuel rate, instantaneous fuel economy, fuel delivery
  pressure and oil pressure, encoded with J1939-71 scaling.
* Route profiles (highway / regional / hilly), payload that varies day to day,
  day-level variation in cooling and fuel pressure, a fleet-wide heatwave,
  sensor noise and a few corrupted log lines.
* Two progressive failure modes that end in a DM1 fault:
    - cooling degradation (fan clutch / radiator): coolant runs hotter under
      load, ending in SPN 110 FMI 0 + SPN 111 FMI 1 (sent as a BAM DM1);
    - fuel supply degradation (filter / lift pump): delivery pressure sags and
      fuel economy drops, ending in SPN 94 FMI 1.
  After the fault the truck is off the road for 2 days and comes back repaired.
* Decoys that are *not* failures: a truck on a hilly route, a truck whose
  payload steps up mid-period, a noisy sensor, and an unrelated battery DTC.

Usage: python fleetsim.py --out data --trucks 24 --days 60 --seed 42
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import os
import random

START = dt.datetime(2026, 8, 3, tzinfo=dt.timezone.utc)  # a Monday
PERIOD_S = 30

PGN_IDS = {
    "EEC1": 0x0CF00400,
    "CCVS": 0x18FEF100,
    "ET1": 0x18FEEE00,
    "LFE": 0x18FEF200,
    "EFLP1": 0x18FEEF00,
    "DM1": 0x18FECA00,
    "TP_CM": 0x1CECFF00,
    "TP_DT": 0x1CEBFF00,
}

COOLING_DTCS = [(110, 0), (111, 1)]
FUEL_DTCS = [(94, 1)]


def u16(v: float) -> tuple[int, int]:
    raw = max(0, min(0xFAFF, int(round(v))))
    return raw & 0xFF, raw >> 8


def u8(v: float) -> int:
    return max(0, min(0xFA, int(round(v))))


def frame(ts: float, veh: str, can_id: int, data: list[int]) -> str:
    return f"({ts:.6f}) {veh} {can_id:08X}#" + "".join(f"{b:02X}" for b in data)


def dtc_bytes(spn: int, fmi: int, oc: int) -> list[int]:
    return [spn & 0xFF, (spn >> 8) & 0xFF, ((spn >> 11) & 0xE0) | (fmi & 0x1F), oc & 0x7F]


def dm1_frames(ts: float, veh: str, dtcs: list[tuple[int, int]], oc: int) -> list[str]:
    lamps = 0x14  # amber + red stop lamp
    payload = [lamps, 0x00]
    for spn, fmi in dtcs:
        payload += dtc_bytes(spn, fmi, oc)
    if len(payload) <= 8:
        payload += [0xFF] * (8 - len(payload))
        return [frame(ts, veh, PGN_IDS["DM1"], payload)]
    # multi-DTC DM1 does not fit in 8 bytes: Broadcast Announce Message (J1939-21)
    size = len(payload)
    packets = (size + 6) // 7
    out = [frame(ts, veh, PGN_IDS["TP_CM"], [32, size & 0xFF, size >> 8, packets, 0xFF, 0xCA, 0xFE, 0x00])]
    for i in range(packets):
        chunk = payload[i * 7:(i + 1) * 7]
        chunk += [0xFF] * (7 - len(chunk))
        out.append(frame(ts + 0.05 * (i + 1), veh, PGN_IDS["TP_DT"], [i + 1] + chunk))
    return out


def no_dtc_frame(ts: float, veh: str) -> str:
    return frame(ts, veh, PGN_IDS["DM1"], [0x00, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF])


def build_fleet(n: int, days: int, rng: random.Random) -> dict:
    trucks = {}
    for i in range(1, n + 1):
        trucks[f"trk{i:02d}"] = {
            "route": rng.choice(["highway", "highway", "regional"]),
            "payload": rng.uniform(0.55, 0.85),
            "noise": 1.0,
            "fault": None,
            "events": [],
        }
    names = sorted(trucks)
    # failures: (truck, mode, failure day, ramp days); spread over the period
    plan = [("trk03", "cooling", 41), ("trk08", "cooling", 52), ("trk14", "cooling", 30),
            ("trk19", "cooling", 47), ("trk22", "cooling", 57), ("trk06", "fuel", 44),
            ("trk10", "fuel", 36), ("trk16", "fuel", 55), ("trk21", "fuel", 49)]
    for veh, mode, day in plan:
        if (START + dt.timedelta(days=day)).weekday() == 6:
            day += 1  # trucks do not run on Sundays: the fault shows on Monday
        if veh in trucks and day < days:
            trucks[veh]["fault"] = {"mode": mode, "fail_day": day, "ramp_days": rng.randint(12, 20)}
    # decoys
    if "trk05" in trucks:
        trucks["trk05"]["route"] = "hilly"
    if "trk17" in trucks:
        trucks["trk17"]["payload_step"] = {"from_day": 35, "payload": 0.98}
    if "trk11" in trucks:
        trucks["trk11"]["noise"] = 2.5
    if "trk12" in trucks:
        trucks["trk12"]["battery_dtc_day"] = 25
    return {"trucks": trucks, "names": names}


def ambient_c(day: int) -> float:
    base = 29.0 + 3.0 * math.sin(2 * math.pi * day / 45.0)
    heat = 6.0 if 20 <= day <= 26 else 0.0  # fleet-wide heatwave
    return base + heat


def degradation(fault: dict | None, day: int) -> float:
    if not fault:
        return 0.0
    start = fault["fail_day"] - fault["ramp_days"]
    if day < start or day > fault["fail_day"]:
        return 0.0
    x = (day - start) / fault["ramp_days"]
    return x ** 1.6  # slow onset, accelerating towards failure


def simulate_day(veh: str, t: dict, day: int, rng: random.Random) -> tuple[list[str], dict | None]:
    date = START + dt.timedelta(days=day)
    route = t["route"]
    payload = t["payload"]
    if "payload_step" in t and day >= t["payload_step"]["from_day"]:
        payload = t["payload_step"]["payload"]
    payload = min(1.0, max(0.3, payload + rng.gauss(0, 0.08)))  # load varies day to day
    day_cool = rng.gauss(0, 1.2)    # day-level variation: traffic, wind, fan cycling
    day_fuelp = rng.gauss(0, 10.0)  # fuel temperature, filter loading, fuel batch
    fault = t["fault"]
    deg = degradation(fault, day)
    mode = fault["mode"] if fault else None
    noise = t["noise"]
    amb = ambient_c(day) + rng.gauss(0, 1.0)

    start_s = date.timestamp() + rng.uniform(5.5, 7.0) * 3600
    duration_s = rng.uniform(3.0, 4.0) * 3600
    steps = int(duration_s // PERIOD_S)
    cruise = {"highway": 84.0, "regional": 68.0, "hilly": 62.0}[route]
    grade_amp = {"highway": 0.08, "regional": 0.15, "hilly": 0.35}[route]

    lines: list[str] = []
    coolant = amb + 5.0
    fault_fired = fault is not None and day == fault["fail_day"]
    fire_at = rng.randint(steps // 3, steps // 2) if fault_fired else None
    dtc_active = False
    record = None
    battery_day = t.get("battery_dtc_day") == day
    battery_at = steps // 2 if battery_day else None

    for k in range(steps):
        ts = start_s + k * PERIOD_S + rng.uniform(0, 0.02)
        phase = k / steps
        # speed profile: depart, cruise with traffic, arrive
        if phase < 0.05:
            speed = cruise * phase / 0.05
        elif phase > 0.95:
            speed = cruise * (1 - phase) / 0.05
        else:
            speed = cruise + rng.gauss(0, 4.0) - (25.0 if rng.random() < 0.04 else 0.0)
        speed = max(0.0, speed)
        grade = grade_amp * math.sin(2 * math.pi * k / 47.0 + sum(map(ord, veh)) % 7)
        load = max(0.05, min(1.0, 0.25 + 0.55 * payload + grade + rng.gauss(0, 0.04))) if speed > 5 else 0.05
        rpm = 650.0 if speed < 3 else 900.0 + 7.5 * speed + 120.0 * load + rng.gauss(0, 25)

        fuel_rate = 3.0 + (0.32 * speed * (0.55 + 0.9 * load)) + rng.gauss(0, 0.6)
        if mode == "fuel":
            fuel_rate *= 1.0 + 0.09 * deg  # poorer atomisation burns more fuel
        fuel_rate = max(1.5, fuel_rate)
        economy = speed / fuel_rate if speed > 5 else 0.0

        # thermostat-controlled coolant: warms up, then settles near target
        target = 84.0 + 7.0 * load + 0.18 * (amb - 28.0) + day_cool
        if route == "hilly":
            target += 2.0
        if mode == "cooling":
            target += 16.0 * deg * (0.4 + load)
        coolant += (target - coolant) * 0.12 + rng.gauss(0, 0.35 * noise)

        fuel_p = 560.0 + day_fuelp - 2.2 * fuel_rate + rng.gauss(0, 6.0 * noise)
        if mode == "fuel":
            fuel_p -= 170.0 * deg * (0.3 + load)
        oil_p = 180.0 + 0.12 * rpm + rng.gauss(0, 8.0)

        lines.append(frame(ts, veh, PGN_IDS["EEC1"], [0xFF, 0xFF, 0xFF, *u16(rpm / 0.125), 0xFF, 0xFF, 0xFF]))
        lines.append(frame(ts + 0.01, veh, PGN_IDS["CCVS"], [0xFF, *u16(speed * 256), 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]))
        lines.append(frame(ts + 0.02, veh, PGN_IDS["ET1"], [u8(coolant + 40), 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]))
        lines.append(frame(ts + 0.03, veh, PGN_IDS["LFE"], [*u16(fuel_rate / 0.05), *u16(economy * 512), 0xFF, 0xFF, 0xFF, 0xFF]))
        lines.append(frame(ts + 0.04, veh, PGN_IDS["EFLP1"], [u8(fuel_p / 4), 0xFF, 0xFF, u8(oil_p / 4), 0xFF, 0xFF, 0xFF, 0xFF]))

        if fire_at is not None and k >= fire_at:
            dtcs = COOLING_DTCS if mode == "cooling" else FUEL_DTCS
            lines.extend(dm1_frames(ts + 0.1, veh, dtcs, oc=min(126, 1 + (k - fire_at) // 4)))
            if not dtc_active:
                record = {"vehicle": veh, "day": day, "date": date.date().isoformat(), "mode": mode,
                          "dtcs": [f"SPN {s} FMI {f}" for s, f in dtcs]}
            dtc_active = True
        elif battery_at is not None and battery_at <= k < battery_at + 40:
            lines.extend(dm1_frames(ts + 0.1, veh, [(168, 4)], oc=1))
        elif k % 20 == 0:
            lines.append(no_dtc_frame(ts + 0.1, veh))

        if rng.random() < 0.0002:  # occasional corrupted line on the logger
            lines.append(f"({ts + 0.2:.6f}) {veh} 18FEEE#7")
    return lines, record


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="data")
    ap.add_argument("--trucks", type=int, default=24)
    ap.add_argument("--days", type=int, default=60)
    ap.add_argument("--seed", type=int, default=42)
    a = ap.parse_args()
    rng = random.Random(a.seed)
    fleet = build_fleet(a.trucks, a.days, rng)
    os.makedirs(os.path.join(a.out, "logs"), exist_ok=True)
    faults, files, frames = [], 0, 0
    for veh in fleet["names"]:
        t = fleet["trucks"][veh]
        vdir = os.path.join(a.out, "logs", veh)
        os.makedirs(vdir, exist_ok=True)
        down_until = -1
        for day in range(a.days):
            date = START + dt.timedelta(days=day)
            if date.weekday() == 6 or day <= down_until:  # Sundays off / in the workshop
                continue
            lines, rec = simulate_day(veh, t, day, rng)
            if rec:
                faults.append(rec)
                down_until = day + 2
            with open(os.path.join(vdir, f"{date.date().isoformat()}.log"), "w") as fh:
                fh.write("\n".join(lines) + "\n")
            files += 1
            frames += len(lines)
    truth = {
        "seed": a.seed, "trucks": a.trucks, "days": a.days, "start": START.date().isoformat(),
        "faults": faults,
        "fault_plan": {v: t["fault"] for v, t in fleet["trucks"].items() if t["fault"]},
        "decoys": {"hilly_route": "trk05", "payload_step": "trk17", "noisy_sensor": "trk11",
                   "unrelated_battery_dtc": "trk12"},
    }
    with open(os.path.join(a.out, "ground_truth.json"), "w") as fh:
        json.dump(truth, fh, indent=2)
    print(f"wrote {files} log files, {frames} lines, {len(faults)} faults -> {a.out}")


if __name__ == "__main__":
    main()
