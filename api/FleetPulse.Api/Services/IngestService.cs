using FleetPulse.Api.Domain;
using FleetPulse.Api.Native;

namespace FleetPulse.Api.Services;

/// <summary>Decodes a candump log with the C library, aggregates it and stores the result.</summary>
public sealed class IngestService(FleetStore store)
{
    public IngestResult Ingest(ReadOnlySpan<byte> log)
    {
        var events = new List<TelemetryEvent>(capacity: Math.Max(16, log.Length / 25));
        J1939Stats stats;
        using (var decoder = new J1939Decoder())
        {
            decoder.Decode(log, e => events.Add(new TelemetryEvent(
                J1939Native.VehicleOf(e), e.Ts, e.Kind, e.Signal, e.Spn, e.Value, e.Fmi, e.Oc, e.Lamp)));
            stats = decoder.Stats;
        }

        var days = TelemetryAggregator.Summarise(events);
        int updated = store.UpsertDays(days);
        var dtcEvents = events.Where(e => e.Kind != TelemetryEvent.SignalKind).ToList();
        store.ApplyDtcEvents(dtcEvents);

        var result = new IngestResult(
            (long)stats.Lines, (long)stats.Frames, (long)stats.Events, (long)stats.ParseErrors,
            (long)stats.TpErrors, (long)stats.TpMessages, updated,
            days.Select(d => d.Vehicle).Distinct().ToList(),
            dtcEvents.Count(e => e.Kind == TelemetryEvent.DtcKind));
        store.LastIngest = result;
        return result;
    }
}
