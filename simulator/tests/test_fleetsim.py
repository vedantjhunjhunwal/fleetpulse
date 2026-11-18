"""Tests for the fleet simulator (run with: python -m pytest simulator/tests)."""
import hashlib
import os
import random
import shutil
import subprocess
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import fleetsim  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DECODER = os.path.join(ROOT, "decoder", "build", "j1939dec")


def run_sim(out, trucks=4, days=10, seed=7):
    subprocess.run([sys.executable, os.path.join(ROOT, "simulator", "fleetsim.py"), "--out", str(out),
                    "--trucks", str(trucks), "--days", str(days), "--seed", str(seed)], check=True,
                   capture_output=True)


def digest(folder):
    h = hashlib.sha256()
    for dirpath, _, files in sorted(os.walk(folder)):
        for f in sorted(files):
            with open(os.path.join(dirpath, f), "rb") as fh:
                h.update(f.encode() + fh.read())
    return h.hexdigest()


# REQ: NFR-04
def test_same_seed_gives_identical_logs(tmp_path):
    run_sim(tmp_path / "a")
    run_sim(tmp_path / "b")
    run_sim(tmp_path / "c", seed=8)
    assert digest(tmp_path / "a") == digest(tmp_path / "b")
    assert digest(tmp_path / "a") != digest(tmp_path / "c")


# REQ: FR-05, FR-06
def test_dm1_encoding_matches_j1939_layout():
    single = fleetsim.dm1_frames(5.0, "trk01", [(94, 1)], oc=3)
    assert single == ["(5.000000) trk01 18FECA00#14005E00010" + "3FFFF"]
    bam = fleetsim.dm1_frames(100.0, "trk03", [(110, 0), (111, 1)], oc=2)
    assert bam[0].endswith("1CECFF00#200A0002FFCAFE00")  # 10 bytes, 2 packets, PGN 0xFECA
    assert bam[1].endswith("1CEBFF00#0114006E0000026F")
    assert bam[2].endswith("1CEBFF00#02000102FFFFFFFF")
    assert fleetsim.dtc_bytes(0x7F000, 5, 1) == [0x00, 0xF0, 0xE5, 0x01]


# REQ: NFR-04
def test_faults_never_land_on_a_sunday():
    fleet = fleetsim.build_fleet(24, 60, random.Random(42))
    for t in fleet["trucks"].values():
        if t["fault"]:
            day = fleetsim.START + fleetsim.dt.timedelta(days=t["fault"]["fail_day"])
            assert day.weekday() != 6


# REQ: FR-03, NFR-04
@pytest.mark.skipif(not os.path.exists(DECODER), reason="build decoder/ first")
def test_decoder_reads_simulated_logs(tmp_path):
    run_sim(tmp_path, trucks=2, days=3)
    logs = sorted(str(p) for p in (tmp_path / "logs").rglob("*.log"))
    out = subprocess.run([DECODER, "--stats", *logs], check=True, capture_output=True, text=True)
    rows = [r.split(",") for r in out.stdout.strip().splitlines()[1:]]
    coolant = [float(r[5]) for r in rows if r[3] == "coolant_temp_c"]
    speed = [float(r[5]) for r in rows if r[3] == "vehicle_speed_kmh"]
    assert len(coolant) > 500 and len(speed) == len(coolant)
    assert 30 < min(coolant) and max(coolant) < 115
    assert max(speed) < 120
    assert "tp_errors=0" in out.stderr
