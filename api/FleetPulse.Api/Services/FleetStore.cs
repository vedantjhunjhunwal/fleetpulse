using FleetPulse.Api.Domain;

namespace FleetPulse.Api.Services;

/// <summary>Thread-safe in-memory store for daily summaries, trouble codes and risk scores.</summary>
public sealed class FleetStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, DateOnly), DailySummary> _days = new();
    private readonly Dictionary<(string, uint, byte), DtcRecord> _dtcs = new();
    private RiskBatch? _risk;
    private IngestResult? _lastIngest;

    /// <summary>Adds or replaces days; re-ingesting a day's log is idempotent.</summary>
    public int UpsertDays(IEnumerable<DailySummary> days)
    {
        lock (_gate)
        {
            int n = 0;
            foreach (var d in days) { _days[(d.Vehicle, d.Date)] = d; n++; }
            return n;
        }
    }

    /// <summary>Applies DM1 events in time order: a DTC event raises/refreshes a code,
    /// a "no active DTC" broadcast clears every code on that truck last seen before it.</summary>
    public void ApplyDtcEvents(IEnumerable<TelemetryEvent> events)
    {
        lock (_gate)
        {
            foreach (var e in events.OrderBy(e => e.Ts))
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(e.Ts * 1000));
                if (e.Kind == TelemetryEvent.DtcKind)
                {
                    var key = (e.Vehicle, e.Spn, e.Fmi);
                    if (!_dtcs.TryGetValue(key, out var r))
                    {
                        _dtcs[key] = r = new DtcRecord { Vehicle = e.Vehicle, Spn = e.Spn, Fmi = e.Fmi, FirstSeen = at, LastSeen = at };
                    }
                    if (at < r.FirstSeen) r.FirstSeen = at;
                    if (at > r.LastSeen) r.LastSeen = at;
                    if (r.ClearedAt is { } c && at > c) r.ClearedAt = null; // came back
                    r.OccurrenceCount = Math.Max(r.OccurrenceCount, e.Oc);
                    r.Lamp |= e.Lamp;
                }
                else if (e.Kind == TelemetryEvent.DtcNoneKind)
                {
                    foreach (var r in _dtcs.Values)
                        if (r.Vehicle == e.Vehicle && r.Active && r.LastSeen < at) r.ClearedAt = at;
                }
            }
        }
    }

    public void SetRisk(RiskBatch batch) { lock (_gate) _risk = batch; }
    public RiskBatch? Risk { get { lock (_gate) return _risk; } }

    public IngestResult? LastIngest
    {
        get { lock (_gate) return _lastIngest; }
        set { lock (_gate) _lastIngest = value; }
    }

    public List<DailySummary> Days(string? vehicle = null)
    {
        lock (_gate)
            return _days.Values.Where(d => vehicle is null || d.Vehicle == vehicle)
                .OrderBy(d => d.Vehicle, StringComparer.Ordinal).ThenBy(d => d.Date).ToList();
    }

    public List<DtcRecord> Dtcs(string? vehicle = null, bool? active = null)
    {
        lock (_gate)
            return _dtcs.Values.Where(r => (vehicle is null || r.Vehicle == vehicle) && (active is null || r.Active == active))
                .OrderByDescending(r => r.LastSeen).ToList();
    }

    public bool HasVehicle(string vehicle)
    {
        lock (_gate) return _days.Keys.Any(k => k.Item1 == vehicle);
    }

    public List<string> Vehicles()
    {
        lock (_gate) return _days.Keys.Select(k => k.Item1).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
    }
}
