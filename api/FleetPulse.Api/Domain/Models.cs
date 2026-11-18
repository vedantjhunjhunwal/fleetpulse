namespace FleetPulse.Api.Domain;

/// <summary>One truck, one operating day, aggregated from decoded J1939 signals.</summary>
public sealed record DailySummary
{
    public required string Vehicle { get; init; }
    public required DateOnly Date { get; init; }
    public int Samples { get; init; }
    public double EngineHours { get; init; }
    public double DistanceKm { get; init; }
    public double FuelL { get; init; }
    public double? KmPerL { get; init; }
    public double IdleShare { get; init; }
    /// <summary>Mean coolant temperature while cruising (&gt; 40 km/h, after a 15-minute warm-up).</summary>
    public double? CoolantCruiseC { get; init; }
    /// <summary>Mean coolant temperature in the day's top quartile of fuel rate (engine under high load).</summary>
    public double? CoolantHighLoadC { get; init; }
    public double? CoolantP95C { get; init; }
    public double? FuelPressureCruiseKpa { get; init; }
    public double? FuelPressureP05Kpa { get; init; }
    public double? OilPressureKpa { get; init; }
    public int DtcEvents { get; init; }
    public int MajorDtcEvents { get; init; }
}

/// <summary>A diagnostic trouble code seen on a truck, with its lifecycle.</summary>
public sealed class DtcRecord
{
    public required string Vehicle { get; init; }
    public required uint Spn { get; init; }
    public required byte Fmi { get; init; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public int OccurrenceCount { get; set; }
    public byte Lamp { get; set; }
    public bool Active => ClearedAt is null;
}

public sealed record RiskScore(string Vehicle, DateOnly Date, double Risk, string Driver);

public sealed record RiskBatch
{
    public required double Threshold { get; init; }
    public string Model { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public required List<RiskScore> Scores { get; init; }
}

public sealed record IngestResult(
    long Lines, long Frames, long Events, long ParseErrors, long TpErrors, long BamMessages,
    int DaysUpdated, IReadOnlyList<string> Vehicles, int DtcEvents);
