using FleetPulse.Api.Domain;

namespace FleetPulse.Api.Services;

/// <summary>A decoded J1939 event in managed form (signal value or DM1 trouble code).</summary>
public readonly record struct TelemetryEvent(
    string Vehicle, double Ts, int Kind, int Signal, uint Spn, double Value, byte Fmi = 0, byte Oc = 0, byte Lamp = 0)
{
    public const int SignalKind = 1, DtcKind = 2, DtcNoneKind = 3;
    public const int EngineSpeed = 0, VehicleSpeed = 1, CoolantTemp = 2, FuelRate = 3,
        FuelEconomy = 4, FuelPressure = 5, OilPressure = 6;

    public DateOnly Date => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(Ts * 1000)).UtcDateTime);
}

/// <summary>Turns a stream of decoded events into one <see cref="DailySummary"/> per truck per day.</summary>
public static class TelemetryAggregator
{
    public const double WarmupSeconds = 900;   // ignore coolant for the first 15 minutes of a trip
    public const double CruiseKmh = 40;
    public const double MaxGapSeconds = 60;    // gaps longer than this are not integrated
    public const double FreshSeconds = 5;      // how long a sample is used to qualify other signals
    public const int MinHighLoadSamples = 20;

    public static List<DailySummary> Summarise(IEnumerable<TelemetryEvent> events) =>
        events.GroupBy(e => (e.Vehicle, e.Date))
              .OrderBy(g => g.Key.Vehicle, StringComparer.Ordinal).ThenBy(g => g.Key.Date)
              .Select(g => SummariseDay(g.Key.Vehicle, g.Key.Date, g.OrderBy(e => e.Ts).ToList()))
              .ToList();

    private static DailySummary SummariseDay(string vehicle, DateOnly date, List<TelemetryEvent> ev)
    {
        double start = ev[0].Ts, end = ev[^1].Ts;
        var speed = new List<(double Ts, double V)>();
        var fuelRate = new List<(double Ts, double V)>();
        var rpm = new List<double>();
        var coolant = new List<(double Ts, double V)>();
        var pressure = new List<(double Ts, double V)>();
        var oil = new List<double>();
        int dtc = 0, major = 0;

        foreach (var e in ev)
        {
            if (e.Kind == TelemetryEvent.DtcKind)
            {
                dtc++;
                if (DtcCatalog.IsMajor(e.Spn)) major++;
                continue;
            }
            if (e.Kind != TelemetryEvent.SignalKind) continue;
            switch (e.Signal)
            {
                case TelemetryEvent.VehicleSpeed: speed.Add((e.Ts, e.Value)); break;
                case TelemetryEvent.FuelRate: fuelRate.Add((e.Ts, e.Value)); break;
                case TelemetryEvent.EngineSpeed: rpm.Add(e.Ts); break;
                case TelemetryEvent.CoolantTemp: coolant.Add((e.Ts, e.Value)); break;
                case TelemetryEvent.FuelPressure: pressure.Add((e.Ts, e.Value)); break;
                case TelemetryEvent.OilPressure: oil.Add(e.Value); break;
            }
        }

        double distance = Integrate(speed), fuel = Integrate(fuelRate);
        int idleSamples = rpm.Count(t => Nearest(speed, t) is { } v && v < 3);
        var cruiseCoolant = new List<double>();
        var loadPairs = new List<(double Coolant, double FuelRate)>();
        foreach (var (t, v) in coolant)
        {
            if (t - start < WarmupSeconds || Nearest(speed, t) is not { } kmh || kmh <= CruiseKmh) continue;
            cruiseCoolant.Add(v);
            if (Nearest(fuelRate, t) is { } fr) loadPairs.Add((v, fr));
        }
        var cruisePressure = pressure.Where(p => Nearest(speed, p.Ts) is { } kmh && kmh > CruiseKmh).Select(p => p.V).ToList();
        int speedSamples = speed.Count, rpmSamples = rpm.Count;

        double? highLoad = null;
        if (loadPairs.Count >= MinHighLoadSamples)
        {
            double p75 = Percentile(loadPairs.Select(p => p.FuelRate).ToList(), 0.75);
            highLoad = loadPairs.Where(p => p.FuelRate >= p75).Average(p => p.Coolant);
        }

        return new DailySummary
        {
            Vehicle = vehicle,
            Date = date,
            Samples = speedSamples,
            EngineHours = R((end - start) / 3600.0),
            DistanceKm = R(distance),
            FuelL = R(fuel),
            KmPerL = fuel > 1 ? R(distance / fuel) : null,
            IdleShare = rpmSamples > 0 ? R((double)idleSamples / rpmSamples) : 0,
            CoolantCruiseC = cruiseCoolant.Count > 0 ? R(cruiseCoolant.Average()) : null,
            CoolantHighLoadC = highLoad is { } h ? R(h) : null,
            CoolantP95C = cruiseCoolant.Count > 0 ? R(Percentile(cruiseCoolant, 0.95)) : null,
            FuelPressureCruiseKpa = cruisePressure.Count > 0 ? R(cruisePressure.Average()) : null,
            FuelPressureP05Kpa = cruisePressure.Count > 0 ? R(Percentile(cruisePressure, 0.05)) : null,
            OilPressureKpa = oil.Count > 0 ? R(oil.Average()) : null,
            DtcEvents = dtc,
            MajorDtcEvents = major,
        };
    }

    /// <summary>Left Riemann sum of a rate (per hour) over time; gaps above MaxGapSeconds are capped.</summary>
    public static double Integrate(List<(double Ts, double V)> series)
    {
        double sum = 0;
        for (int i = 1; i < series.Count; i++)
            sum += series[i - 1].V * Math.Min(series[i].Ts - series[i - 1].Ts, MaxGapSeconds) / 3600.0;
        return sum;
    }

    /// <summary>Value of the sample nearest in time to <paramref name="t"/> (series sorted by time),
    /// or null if none lies within <see cref="FreshSeconds"/>.</summary>
    public static double? Nearest(List<(double Ts, double V)> series, double t)
    {
        if (series.Count == 0) return null;
        int lo = 0, hi = series.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (series[mid].Ts < t) lo = mid + 1; else hi = mid;
        }
        int best = lo;
        if (lo > 0 && Math.Abs(series[lo - 1].Ts - t) <= Math.Abs(series[lo].Ts - t)) best = lo - 1;
        return Math.Abs(series[best].Ts - t) <= FreshSeconds ? series[best].V : null;
    }

    /// <summary>Linear-interpolated percentile, q in [0, 1].</summary>
    public static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0) throw new ArgumentException("no values", nameof(values));
        var s = values.OrderBy(v => v).ToList();
        double pos = q * (s.Count - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    private static double R(double v) => Math.Round(v, 3);
}
