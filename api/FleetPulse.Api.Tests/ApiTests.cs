using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FleetPulse.Api.Tests;

/// <summary>End-to-end tests: real Kestrel host, real HTTP, real C decoder through P/Invoke.</summary>
public static class ApiTests
{
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync()
    {
        var app = FleetPulseApp.Create([], "http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Host { App = app, Http = new HttpClient { BaseAddress = new Uri(address) } };
    }

    /// <summary>A 40-minute trip for one truck, then a two-code DM1 sent with BAM and one corrupted line.</summary>
    private static string SampleLog(string veh = "trk01")
    {
        var sb = new StringBuilder();
        const double t0 = 1_788_000_000;
        string L(double t, string frame) => $"({t.ToString("F6", CultureInfo.InvariantCulture)}) {veh} {frame}\n";
        for (int k = 0; k < 80; k++)
        {
            double t = t0 + 30 * k;
            sb.Append(L(t, "0CF00400#FFFFFF0032FFFFFF"));           // 1600 rpm
            sb.Append(L(t + 0.01, "18FEF100#FF0050FFFFFFFFFF"));    // 80 km/h
            sb.Append(L(t + 0.02, "18FEEE00#7AFFFFFFFFFFFFFF"));    // 82 C
            sb.Append(L(t + 0.03, "18FEF200#58020006FFFFFFFF"));    // 30 L/h, 3 km/L
            sb.Append(L(t + 0.04, "18FEEF00#7DFFFF5AFFFFFFFF"));    // 500 kPa fuel, 360 kPa oil
        }
        double te = t0 + 30 * 80;
        sb.Append(L(te, "1CECFF00#200A0002FFCAFE00"));
        sb.Append(L(te + 0.05, "1CEBFF00#0114006E0000006F"));
        sb.Append(L(te + 0.10, "1CEBFF00#02000102FFFFFFFF"));
        sb.Append($"(1788002500.0) {veh} 18FEEE#7\n");
        return sb.ToString();
    }

    private static Task<HttpResponseMessage> PostLog(HttpClient http, string body, string type = "text/plain") =>
        http.PostAsync("/api/ingest", new StringContent(body, Encoding.UTF8, type));

    // REQ: FR-07
    [Test]
    public static async Task Health_reports_decoder_version()
    {
        await using var h = await StartAsync();
        var json = await h.Http.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("1.0.0", json.GetProperty("decoder").GetString());
    }

    // REQ: FR-05, FR-06, FR-07, FR-08
    [Test]
    public static async Task Ingest_decodes_through_the_c_library_and_aggregates()
    {
        await using var h = await StartAsync();
        var res = await PostLog(h.Http, SampleLog());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, r.GetProperty("lines").GetInt32());
        Assert.Equal(403, r.GetProperty("frames").GetInt32());
        Assert.Equal(1, r.GetProperty("parseErrors").GetInt32());
        Assert.Equal(1, r.GetProperty("bamMessages").GetInt32());
        Assert.Equal(2, r.GetProperty("dtcEvents").GetInt32());

        var v = await h.Http.GetFromJsonAsync<JsonElement>("/api/vehicles/trk01");
        var day = v.GetProperty("days")[0];
        Assert.Near(80.0 * 79 * 30 / 3600, day.GetProperty("distanceKm").GetDouble(), 1e-3, "distance");
        Assert.Near(82, day.GetProperty("coolantCruiseC").GetDouble(), 1e-9, "coolant");
        Assert.Near(500, day.GetProperty("fuelPressureCruiseKpa").GetDouble(), 1e-9, "fuel pressure");
        Assert.Equal("down", v.GetProperty("summary").GetProperty("riskBand").GetString());

        var faults = await h.Http.GetFromJsonAsync<JsonElement>("/api/faults?active=true");
        Assert.Equal(2, faults.GetArrayLength());
        var spns = faults.EnumerateArray().Select(f => f.GetProperty("spn").GetInt32()).OrderBy(x => x).ToList();
        Assert.Equal("110,111", string.Join(',', spns));
        Assert.Equal(3, faults[0].GetProperty("lamp").GetInt32());
    }

    // REQ: FR-16
    [Test]
    public static async Task Ingest_rejects_bad_requests_with_problem_details()
    {
        await using var h = await StartAsync();
        var wrongType = await PostLog(h.Http, "{}", "application/json");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal("application/problem+json", wrongType.Content.Headers.ContentType?.MediaType);
        var empty = await PostLog(h.Http, "");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var garbage = await PostLog(h.Http, "not a can log\nstill not\n");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);
        var problem = await garbage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("detail").GetString()!.Contains("2 malformed"), "detail");
    }

    // REQ: FR-16
    [Test]
    public static async Task Unknown_vehicle_is_404()
    {
        await using var h = await StartAsync();
        var res = await h.Http.GetAsync("/api/vehicles/trk99");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // REQ: FR-10
    [Test]
    public static async Task Exports_daily_features_csv()
    {
        await using var h = await StartAsync();
        await PostLog(h.Http, SampleLog("trk01"));
        await PostLog(h.Http, SampleLog("trk02"));
        var res = await h.Http.GetAsync("/api/export/daily-features.csv");
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        var lines = (await res.Content.ReadAsStringAsync()).TrimEnd().Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.True(lines[1].StartsWith("trk01,2026-08-29,"), lines[1]);
        Assert.True(lines[2].EndsWith(",2,2"), "dtc columns: " + lines[2]);
    }

    // REQ: FR-12, FR-16
    [Test]
    public static async Task Risk_scores_feed_the_maintenance_queue()
    {
        await using var h = await StartAsync();
        await PostLog(h.Http, SampleLog("trk01"));
        var clean = string.Join('\n', SampleLog("trk02").Split('\n').Where(l => !l.Contains("1CE")));
        await PostLog(h.Http, clean);

        var bad = await h.Http.PostAsJsonAsync("/api/risk", new
        {
            threshold = 0.4, scores = new[] { new { vehicle = "trk77", date = "2026-08-29", risk = 0.9, driver = "cooling" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("scores[0]", out _), "per-score error");

        var ok = await h.Http.PostAsJsonAsync("/api/risk", new
        {
            threshold = 0.4, model = "test",
            scores = new[] { new { vehicle = "trk02", date = "2026-08-29", risk = 0.71, driver = "fuel" } },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var queue = await h.Http.GetFromJsonAsync<JsonElement>("/api/maintenance");
        Assert.Equal(2, queue.GetArrayLength());
        Assert.Equal("trk01", queue[0].GetProperty("vehicle").GetString());
        Assert.Equal("down", queue[0].GetProperty("band").GetString());
        Assert.Equal("trk02", queue[1].GetProperty("vehicle").GetString());
        Assert.Equal("high", queue[1].GetProperty("band").GetString());
        Assert.True(queue[1].GetProperty("action").GetString()!.Contains("fuel filters"), "fuel action");
    }

    // REQ: FR-13
    [Test]
    public static async Task Fleet_kpis_summarise_the_fleet()
    {
        await using var h = await StartAsync();
        await PostLog(h.Http, SampleLog("trk01"));
        await PostLog(h.Http, SampleLog("trk02"));
        var k = await h.Http.GetFromJsonAsync<JsonElement>("/api/fleet/kpis");
        Assert.Equal(2, k.GetProperty("trucks").GetInt32());
        Assert.Equal(2, k.GetProperty("operatingDays").GetInt32());
        Assert.Equal(4, k.GetProperty("activeMajorDtcs").GetInt32());
        Assert.Near(2 * 80.0 * 79 * 30 / 3600, k.GetProperty("totalKm").GetDouble(), 0.06, "km");
        Assert.Near(80.0 / 30, k.GetProperty("fleetKmPerL").GetDouble(), 1e-3, "km/L");
    }
}
