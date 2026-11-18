using System.Globalization;
using System.Text;
using FleetPulse.Api.Domain;
using FleetPulse.Api.Native;
using FleetPulse.Api.Services;

namespace FleetPulse.Api;

/// <summary>Builds the FleetPulse web API. Used by Program.cs and by the integration tests.</summary>
public static class FleetPulseApp
{
    public const long MaxLogBytes = 64L * 1024 * 1024;

    public static WebApplication Create(string[] args, string? urls = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (urls is not null) builder.WebHost.UseUrls(urls);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxLogBytes);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<FleetStore>();
        builder.Services.AddSingleton<IngestService>();
        builder.Services.AddProblemDetails();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.WithOrigins(builder.Configuration["Cors:Origins"]?.Split(',') ?? ["http://localhost:4200"])
             .AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors();
        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", decoder = J1939Native.Version() }));

        // REQ FR-07: ingest one or more trucks' candump logs (text/plain)
        api.MapPost("/ingest", async (HttpRequest req, IngestService ingest) =>
        {
            var type = req.ContentType ?? "";
            if (!type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) &&
                !type.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Send the candump log as text/plain.", statusCode: StatusCodes.Status415UnsupportedMediaType);
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            if (ms.Length == 0)
                return Results.Problem("The request body is empty.", statusCode: StatusCodes.Status400BadRequest);
            var result = ingest.Ingest(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            if (result.Frames == 0)
                return Results.Problem($"No valid J1939 frames found ({result.ParseErrors} malformed lines).",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(result);
        });

        api.MapGet("/fleet/kpis", (FleetStore store) =>
        {
            var days = store.Days();
            var risk = store.Risk;
            var active = store.Dtcs(active: true);
            double km = days.Sum(d => d.DistanceKm), fuel = days.Sum(d => d.FuelL);
            return Results.Ok(new
            {
                trucks = store.Vehicles().Count,
                operatingDays = days.Count,
                totalKm = Math.Round(km, 1),
                fleetKmPerL = fuel > 0 ? Math.Round(km / fuel, 3) : (double?)null,
                activeDtcs = active.Count,
                activeMajorDtcs = active.Count(r => DtcCatalog.IsMajor(r.Spn)),
                highRisk = risk?.Scores.Count(s => s.Risk >= risk.Threshold) ?? 0,
                riskModel = risk?.Model,
                lastIngest = store.LastIngest,
            });
        });

        api.MapGet("/vehicles", (FleetStore store) => Results.Ok(store.Vehicles().Select(v => VehicleRow(store, v))));

        api.MapGet("/vehicles/{id}", (string id, FleetStore store) =>
        {
            if (!store.HasVehicle(id))
                return Results.Problem($"Unknown vehicle '{id}'.", statusCode: StatusCodes.Status404NotFound);
            return Results.Ok(new
            {
                summary = VehicleRow(store, id),
                days = store.Days(id),
                dtcs = store.Dtcs(id).Select(DtcView),
            });
        });

        api.MapGet("/faults", (bool? active, FleetStore store) => Results.Ok(store.Dtcs(active: active).Select(DtcView)));

        // REQ FR-10: feature export consumed by the R analytics
        api.MapGet("/export/daily-features.csv", (FleetStore store) =>
            Results.Text(DailyFeaturesCsv(store.Days()), "text/csv", Encoding.UTF8));

        // REQ FR-12, FR-16: accept validated risk scores from the R model
        api.MapPost("/risk", (RiskBatch batch, FleetStore store) =>
        {
            var errors = ValidateRisk(batch, store);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            store.SetRisk(batch);
            return Results.Ok(new { accepted = batch.Scores.Count, batch.Threshold, batch.Model });
        });

        api.MapGet("/maintenance", (FleetStore store) => Results.Ok(MaintenanceQueue(store)));
    }

    private static object VehicleRow(FleetStore store, string v)
    {
        var days = store.Days(v);
        var dtcs = store.Dtcs(v, active: true);
        var risk = store.Risk;
        var score = risk?.Scores.FirstOrDefault(s => s.Vehicle == v);
        double km = days.Sum(d => d.DistanceKm), fuel = days.Sum(d => d.FuelL);
        return new
        {
            vehicle = v,
            days = days.Count,
            lastDay = days.LastOrDefault()?.Date,
            totalKm = Math.Round(km, 1),
            kmPerL = fuel > 0 ? Math.Round(km / fuel, 3) : (double?)null,
            activeDtcs = dtcs.Count,
            risk = score?.Risk,
            riskBand = Band(score, risk?.Threshold, dtcs.Any(r => DtcCatalog.IsMajor(r.Spn))),
        };
    }

    private static object DtcView(DtcRecord r) => new
    {
        r.Vehicle, r.Spn, r.Fmi,
        spnName = DtcCatalog.SpnName(r.Spn),
        fmiText = DtcCatalog.FmiText(r.Fmi),
        system = DtcCatalog.SystemOf(r.Spn).ToString(),
        major = DtcCatalog.IsMajor(r.Spn),
        r.FirstSeen, r.LastSeen, r.ClearedAt, r.OccurrenceCount, r.Lamp, r.Active,
    };

    public static string Band(RiskScore? score, double? threshold, bool majorFaultActive)
    {
        if (majorFaultActive) return "down";
        if (score is null || threshold is null) return "unscored";
        if (score.Risk >= threshold) return "high";
        return score.Risk >= threshold / 2 ? "watch" : "normal";
    }

    public static List<object> MaintenanceQueue(FleetStore store)
    {
        var risk = store.Risk;
        var rows = new List<(int Rank, double Risk, object Row)>();
        foreach (var v in store.Vehicles())
        {
            var active = store.Dtcs(v, active: true).Where(r => DtcCatalog.IsMajor(r.Spn)).ToList();
            var score = risk?.Scores.FirstOrDefault(s => s.Vehicle == v);
            var band = Band(score, risk?.Threshold, active.Count > 0);
            if (band is "normal" or "unscored") continue;
            var system = active.Count > 0 ? DtcCatalog.SystemOf(active[0].Spn) : DtcCatalog.ParseSystem(score!.Driver);
            int rank = band switch { "down" => 0, "high" => 1, _ => 2 };
            rows.Add((rank, score?.Risk ?? 1, new
            {
                vehicle = v, band, risk = score?.Risk, system = system.ToString(),
                action = DtcCatalog.Action(system),
                activeFaults = active.Select(r => $"SPN {r.Spn} FMI {r.Fmi}: {DtcCatalog.SpnName(r.Spn)}").ToList(),
            }));
        }
        return rows.OrderBy(r => r.Rank).ThenByDescending(r => r.Risk).Select(r => r.Row).ToList();
    }

    public static Dictionary<string, string[]> ValidateRisk(RiskBatch? batch, FleetStore store)
    {
        var errors = new Dictionary<string, string[]>();
        if (batch is null) { errors["body"] = ["A risk batch is required."]; return errors; }
        if (batch.Threshold is <= 0 or >= 1) errors["threshold"] = ["Threshold must be between 0 and 1 (exclusive)."];
        if (batch.Scores is null || batch.Scores.Count == 0) { errors["scores"] = ["At least one score is required."]; return errors; }
        for (int i = 0; i < batch.Scores.Count; i++)
        {
            var s = batch.Scores[i];
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(s.Vehicle) || !store.HasVehicle(s.Vehicle)) problems.Add($"Unknown vehicle '{s.Vehicle}'.");
            if (double.IsNaN(s.Risk) || s.Risk < 0 || s.Risk > 1) problems.Add("Risk must be within [0, 1].");
            if (string.IsNullOrWhiteSpace(s.Driver)) problems.Add("Driver is required.");
            if (problems.Count > 0) errors[$"scores[{i}]"] = problems.ToArray();
        }
        if (batch.Scores.GroupBy(s => s.Vehicle).Any(g => g.Count() > 1)) errors["scores"] = ["Each vehicle may appear once."];
        return errors;
    }

    public static readonly string[] CsvColumns =
    {
        "vehicle", "date", "distance_km", "fuel_l", "km_per_l", "idle_share", "engine_hours",
        "coolant_cruise_c", "coolant_highload_c", "coolant_p95_c", "fuel_pressure_cruise_kpa",
        "fuel_pressure_p05_kpa", "oil_pressure_kpa", "dtc_events", "major_dtc_events",
    };

    public static string DailyFeaturesCsv(IEnumerable<DailySummary> days)
    {
        var ci = CultureInfo.InvariantCulture;
        string N(double? v) => v is { } x ? x.ToString("0.###", ci) : "NA";
        var sb = new StringBuilder(string.Join(',', CsvColumns)).Append('\n');
        foreach (var d in days)
        {
            sb.Append(d.Vehicle).Append(',').Append(d.Date.ToString("yyyy-MM-dd", ci)).Append(',')
              .Append(N(d.DistanceKm)).Append(',').Append(N(d.FuelL)).Append(',').Append(N(d.KmPerL)).Append(',')
              .Append(N(d.IdleShare)).Append(',').Append(N(d.EngineHours)).Append(',')
              .Append(N(d.CoolantCruiseC)).Append(',').Append(N(d.CoolantHighLoadC)).Append(',').Append(N(d.CoolantP95C)).Append(',')
              .Append(N(d.FuelPressureCruiseKpa)).Append(',').Append(N(d.FuelPressureP05Kpa)).Append(',')
              .Append(N(d.OilPressureKpa)).Append(',').Append(d.DtcEvents).Append(',').Append(d.MajorDtcEvents).Append('\n');
        }
        return sb.ToString();
    }
}
