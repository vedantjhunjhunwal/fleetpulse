using System.Text.Json;
using FleetPulse.Api.Domain;
using FleetPulse.Api.Services;

namespace FleetPulse.Api.Tests;

/// <summary>Unit tests for aggregation, the store, the DTC catalog and validation (no native code).</summary>
public static class DomainTests
{
    private const double T0 = 1_788_000_000; // 2026-08-29 10:40:00 UTC

    private static TelemetryEvent Sig(double ts, int signal, double v, string veh = "trk01") =>
        new(veh, ts, TelemetryEvent.SignalKind, signal, 0, v);

    /// <summary>One sample every 30 s: speed, coolant, fuel rate and fuel pressure.</summary>
    private static List<TelemetryEvent> Trip(int samples, Func<int, double> speed, Func<int, double> coolant,
        Func<int, double> fuel, string veh = "trk01", double t0 = T0)
    {
        var ev = new List<TelemetryEvent>();
        for (int k = 0; k < samples; k++)
        {
            double t = t0 + 30 * k;
            ev.Add(Sig(t, TelemetryEvent.EngineSpeed, 1400, veh));
            ev.Add(Sig(t + 0.01, TelemetryEvent.VehicleSpeed, speed(k), veh));
            ev.Add(Sig(t + 0.02, TelemetryEvent.CoolantTemp, coolant(k), veh));
            ev.Add(Sig(t + 0.03, TelemetryEvent.FuelRate, fuel(k), veh));
            ev.Add(Sig(t + 0.04, TelemetryEvent.FuelPressure, 500, veh));
        }
        return ev;
    }

    // REQ: FR-08
    [Test]
    public static void Integrates_distance_and_fuel_over_the_trip()
    {
        // 121 samples 30 s apart = exactly 1 h at 60 km/h and 30 L/h
        var day = TelemetryAggregator.Summarise(Trip(121, _ => 60, _ => 85, _ => 30)).Single();
        Assert.Near(60, day.DistanceKm, 1e-3, "distance");
        Assert.Near(30, day.FuelL, 1e-3, "fuel");
        Assert.Near(2, day.KmPerL, 1e-3, "km/L");
        Assert.Near(1, day.EngineHours, 1e-3, "engine hours");
        Assert.Equal(121, day.Samples);
    }

    // REQ: FR-08
    [Test]
    public static void Caps_long_gaps_when_integrating()
    {
        var ev = new List<TelemetryEvent>
        {
            Sig(T0, TelemetryEvent.VehicleSpeed, 72),
            Sig(T0 + 3600, TelemetryEvent.VehicleSpeed, 72), // logger gap of 1 h: only 60 s counted
        };
        Assert.Near(72.0 * 60 / 3600, TelemetryAggregator.Summarise(ev).Single().DistanceKm, 1e-3);
    }

    // REQ: FR-08
    [Test]
    public static void Coolant_excludes_warm_up_and_low_speed()
    {
        // first 15 min at 60 C (warm-up), then 90 C at speed, with a slow 20 km/h stretch at 120 C
        var ev = Trip(80, k => k is >= 50 and < 60 ? 20 : 80, k => k < 30 ? 60 : k is >= 50 and < 60 ? 120 : 90, _ => 30);
        var day = TelemetryAggregator.Summarise(ev).Single();
        Assert.Near(90, day.CoolantCruiseC, 1e-9, "cruise coolant");
        Assert.Near(90, day.CoolantP95C, 1e-9, "p95");
    }

    // REQ: FR-08
    [Test]
    public static void High_load_coolant_uses_top_quartile_of_fuel_rate()
    {
        // fuel rate alternates 20 / 40 L/h; coolant is 88 at 20 L/h and 96 at 40 L/h
        var ev = Trip(100, _ => 80, k => k % 2 == 0 ? 88 : 96, k => k % 2 == 0 ? 20 : 40);
        var day = TelemetryAggregator.Summarise(ev).Single();
        Assert.Near(96, day.CoolantHighLoadC, 1e-9, "high-load coolant");
        Assert.Near(92, day.CoolantCruiseC, 0.2, "cruise coolant");
    }

    // REQ: FR-08
    [Test]
    public static void Groups_by_vehicle_and_utc_day()
    {
        var ev = Trip(10, _ => 80, _ => 90, _ => 30, "trk02")
            .Concat(Trip(10, _ => 80, _ => 90, _ => 30, "trk01"))
            .Concat(Trip(10, _ => 80, _ => 90, _ => 30, "trk01", T0 + 86_400));
        var days = TelemetryAggregator.Summarise(ev);
        Assert.Equal(3, days.Count);
        Assert.Equal("trk01", days[0].Vehicle);
        Assert.Equal(new DateOnly(2026, 8, 29), days[0].Date);
        Assert.Equal(new DateOnly(2026, 8, 30), days[1].Date);
        Assert.Equal("trk02", days[2].Vehicle);
    }

    // REQ: FR-08
    [Test]
    public static void Percentile_and_nearest_helpers()
    {
        Assert.Near(2.5, TelemetryAggregator.Percentile([1, 2, 3, 4], 0.5));
        Assert.Near(4, TelemetryAggregator.Percentile([4, 1, 3, 2], 1.0));
        Assert.Throws<ArgumentException>(() => TelemetryAggregator.Percentile([], 0.5));
        var s = new List<(double, double)> { (10, 1), (20, 2), (30, 3) };
        Assert.Equal(2.0, TelemetryAggregator.Nearest(s, 21));
        Assert.Equal(3.0, TelemetryAggregator.Nearest(s, 34));
        Assert.Null(TelemetryAggregator.Nearest(s, 40), "outside the freshness window");
    }

    // REQ: FR-08, FR-09
    [Test]
    public static void Counts_major_and_minor_dtc_events()
    {
        var ev = Trip(5, _ => 80, _ => 90, _ => 30);
        ev.Add(new TelemetryEvent("trk01", T0 + 10, TelemetryEvent.DtcKind, -1, 110, 0, 0, 1, 3));
        ev.Add(new TelemetryEvent("trk01", T0 + 11, TelemetryEvent.DtcKind, -1, 168, 0, 4, 1, 1));
        var day = TelemetryAggregator.Summarise(ev).Single();
        Assert.Equal(2, day.DtcEvents);
        Assert.Equal(1, day.MajorDtcEvents);
    }

    // REQ: FR-09
    [Test]
    public static void Dtc_lifecycle_raise_refresh_clear_and_return()
    {
        var store = new FleetStore();
        TelemetryEvent Dtc(double t, byte oc) => new("trk03", t, TelemetryEvent.DtcKind, -1, 110, 0, 0, oc, 3);
        TelemetryEvent None(double t) => new("trk03", t, TelemetryEvent.DtcNoneKind, -1, 0, 0);
        store.ApplyDtcEvents([Dtc(T0, 1), Dtc(T0 + 30, 2)]);
        var r = store.Dtcs("trk03").Single();
        Assert.True(r.Active, "active after DM1");
        Assert.Equal(2, r.OccurrenceCount);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds((long)T0 + 30), r.LastSeen);
        store.ApplyDtcEvents([None(T0 + 60)]);
        Assert.True(!r.Active, "cleared by 'no active DTC'");
        store.ApplyDtcEvents([None(T0 + 10)]); // an older broadcast does not change anything
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds((long)T0 + 60), r.ClearedAt!.Value);
        store.ApplyDtcEvents([Dtc(T0 + 90, 1)]);
        Assert.True(r.Active, "returned");
        Assert.Equal(1, store.Dtcs().Count);
    }

    // REQ: FR-07, FR-08
    [Test]
    public static void Reingesting_a_day_replaces_it()
    {
        var store = new FleetStore();
        store.UpsertDays(TelemetryAggregator.Summarise(Trip(10, _ => 80, _ => 90, _ => 30)));
        store.UpsertDays(TelemetryAggregator.Summarise(Trip(10, _ => 50, _ => 90, _ => 30)));
        var days = store.Days("trk01");
        Assert.Equal(1, days.Count);
        Assert.Near(50.0 * 270 / 3600, days[0].DistanceKm, 1e-3);
    }

    // REQ: FR-09
    [Test]
    public static void Catalog_describes_codes_and_actions()
    {
        Assert.Equal("Engine coolant temperature", DtcCatalog.SpnName(110));
        Assert.Equal("SPN 9999", DtcCatalog.SpnName(9999));
        Assert.Equal("Data valid but below normal operating range - most severe", DtcCatalog.FmiText(1));
        Assert.Equal("Condition exists", DtcCatalog.FmiText(31));
        Assert.True(DtcCatalog.IsMajor(94) && DtcCatalog.IsMajor(111) && !DtcCatalog.IsMajor(168), "major flags");
        Assert.Equal(TruckSystem.Fuel, DtcCatalog.SystemOf(94));
        Assert.True(DtcCatalog.Action(TruckSystem.Cooling).Contains("fan clutch"), "cooling action");
        Assert.Equal(TruckSystem.Cooling, DtcCatalog.ParseSystem("COOLING"));
        Assert.Equal(TruckSystem.Other, DtcCatalog.ParseSystem("tyres"));
    }

    // REQ: FR-10
    [Test]
    public static void Csv_export_has_fixed_columns_and_NA_for_missing()
    {
        var day = new DailySummary { Vehicle = "trk01", Date = new DateOnly(2026, 9, 1), DistanceKm = 1234.5, KmPerL = null };
        var lines = FleetPulseApp.DailyFeaturesCsv([day]).TrimEnd('\n').Split('\n');
        Assert.Equal(string.Join(',', FleetPulseApp.CsvColumns), lines[0]);
        var cells = lines[1].Split(',');
        Assert.Equal(FleetPulseApp.CsvColumns.Length, cells.Length);
        Assert.Equal("2026-09-01", cells[1]);
        Assert.Equal("1234.5", cells[2]);
        Assert.Equal("NA", cells[4]);
    }

    private static FleetStore StoreWith(params string[] vehicles)
    {
        var store = new FleetStore();
        foreach (var v in vehicles) store.UpsertDays(TelemetryAggregator.Summarise(Trip(5, _ => 80, _ => 90, _ => 30, v)));
        return store;
    }

    // REQ: FR-16
    [Test]
    public static void Risk_validation_reports_each_problem()
    {
        var store = StoreWith("trk01");
        var bad = new RiskBatch
        {
            Threshold = 1.5,
            Scores = [new("trk01", new DateOnly(2026, 9, 1), 1.2, "cooling"), new("trk99", new DateOnly(2026, 9, 1), 0.2, "")],
        };
        var errors = FleetPulseApp.ValidateRisk(bad, store);
        Assert.True(errors.ContainsKey("threshold"), "threshold");
        Assert.True(errors["scores[0]"].Any(e => e.Contains("Risk")), "risk range");
        Assert.Equal(2, errors["scores[1]"].Length);
        Assert.True(FleetPulseApp.ValidateRisk(new RiskBatch { Threshold = 0.4, Scores = [] }, store).ContainsKey("scores"), "empty");
        var dup = new RiskBatch { Threshold = 0.4, Scores = [new("trk01", default, 0.1, "fuel"), new("trk01", default, 0.2, "fuel")] };
        Assert.True(FleetPulseApp.ValidateRisk(dup, store).ContainsKey("scores"), "duplicates");
    }

    // REQ: FR-12
    [Test]
    public static void Risk_bands()
    {
        var s = new RiskScore("trk01", default, 0.30, "cooling");
        Assert.Equal("down", FleetPulseApp.Band(s, 0.5, majorFaultActive: true));
        Assert.Equal("unscored", FleetPulseApp.Band(null, null, false));
        Assert.Equal("high", FleetPulseApp.Band(s with { Risk = 0.5 }, 0.5, false));
        Assert.Equal("watch", FleetPulseApp.Band(s, 0.5, false));
        Assert.Equal("normal", FleetPulseApp.Band(s with { Risk = 0.2 }, 0.5, false));
    }

    // REQ: FR-12
    [Test]
    public static void Maintenance_queue_orders_down_then_risk()
    {
        var store = StoreWith("trk01", "trk02", "trk03", "trk04");
        store.ApplyDtcEvents([new("trk04", T0 + 100, TelemetryEvent.DtcKind, -1, 94, 0, 1, 1, 3)]);
        store.SetRisk(new RiskBatch
        {
            Threshold = 0.5,
            Scores =
            [
                new("trk01", default, 0.55, "cooling"), new("trk02", default, 0.90, "fuel"),
                new("trk03", default, 0.05, "fuel"), new("trk04", default, 0.10, "fuel"),
            ],
        });
        var q = FleetPulseApp.MaintenanceQueue(store).Select(o => JsonSerializer.SerializeToElement(o)).ToList();
        string F(int i, string name) => q[i].GetProperty(name).GetString()!;
        Assert.Equal(3, q.Count);
        Assert.Equal("trk04", F(0, "vehicle"));
        Assert.Equal("down", F(0, "band"));
        Assert.Equal("trk02", F(1, "vehicle"));
        Assert.Equal("Fuel", F(1, "system"));
        Assert.Equal("trk01", F(2, "vehicle"));
        Assert.True(F(2, "action").Contains("radiator"), "cooling action");
    }
}
