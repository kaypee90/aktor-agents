using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>Analytics through the real API and Postgres (docs/analytics.md): a finished run shows up
/// in the totals, roles, tools (with timings), usage by user and run lists; filters narrow it; other
/// organizations see nothing of it.</summary>
public sealed class AnalyticsApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private const string Goal = "Research whether we should build an AI-powered property management SaaS. Produce a market, technical and business analysis.";

    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task A_finished_run_shows_up_with_its_spend_roles_and_tool_times_and_filters_narrow_it()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Analytics"));
        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("AnalyticsOther"));

        var started = await Json(await api.PostAsJsonAsync("/api/tasks", new { goal = Goal }));
        var taskId = started.GetProperty("task_id").GetString()!;
        await Json(await api.GetAsync($"/api/tasks/{taskId}/wait?timeout_seconds=90"));
        // Tool calls and spend are saved by the background writer just after the run ends.
        JsonElement a = default;
        for (var i = 0; i < 20; i++)
        {
            a = await Json(await api.GetAsync("/api/analytics?range=24h"));
            if (a.GetProperty("by_tool").EnumerateArray().Any(t => t.GetProperty("tool").GetString() == "complete_task")) break;
            await Task.Delay(500);
        }

        var totals = a.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("runs").GetInt32());
        Assert.True(totals.GetProperty("tokens").GetInt64() > 0);
        Assert.True(totals.GetProperty("agents").GetInt32() > 1, "the demo goal is done by a team");
        Assert.Equal("hour", a.GetProperty("range").GetProperty("bucket").GetString());
        Assert.Equal(1, a.GetProperty("series").EnumerateArray().Sum(p => p.GetProperty("runs").GetInt32()));
        Assert.NotEmpty(a.GetProperty("by_role").EnumerateArray());
        Assert.Equal(taskId, a.GetProperty("top_by_cost")[0].GetProperty("task_id").GetString());

        // Tool times are measured from now on.
        var write = a.GetProperty("by_tool").EnumerateArray().First(t => t.GetProperty("tool").GetString() == "filesystem_write");
        Assert.True(write.GetProperty("calls").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Number, write.GetProperty("avg_duration_ms").ValueKind);

        // Usage by user: the test client signs in with an API key, so the run is that key's, by its name.
        var byUser = Assert.Single(a.GetProperty("by_user").EnumerateArray());
        var starter = byUser.GetProperty("user").GetString()!;
        Assert.Equal("api_key", byUser.GetProperty("kind").GetString());
        Assert.StartsWith("key:", starter);
        Assert.Equal("API key · Analytics test key", byUser.GetProperty("name").GetString());
        Assert.Equal(1, byUser.GetProperty("runs").GetInt32());
        Assert.Equal(totals.GetProperty("cost_usd").GetDecimal(), byUser.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(starter, a.GetProperty("top_by_cost")[0].GetProperty("started_by").GetString());

        // Filters.
        Assert.Equal(1, (await Json(await api.GetAsync($"/api/analytics?range=24h&user={Uri.EscapeDataString(starter)}"))).GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(0, (await Json(await api.GetAsync("/api/analytics?range=24h&user=unknown"))).GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(0, (await Json(await api.GetAsync("/api/analytics?range=24h&user=key%3Anone"))).GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(0, (await Json(await api.GetAsync("/api/analytics?range=24h&status=failed"))).GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(0, (await Json(await api.GetAsync("/api/analytics?range=24h&source=mcp"))).GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(1, (await Json(await api.GetAsync("/api/analytics?range=24h&q=property%20management"))).GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(0, (await Json(await api.GetAsync("/api/analytics?range=24h&q=no%20such%20goal"))).GetProperty("totals").GetProperty("runs").GetInt32());
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-3).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        Assert.Equal(0, (await Json(await api.GetAsync($"/api/analytics?from={from}&to={to}"))).GetProperty("totals").GetProperty("runs").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/analytics?range=5y")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/analytics?range=7d&status=weird")).StatusCode);

        // Another organization sees none of it.
        Assert.Equal(0, (await Json(await other.GetAsync("/api/analytics?range=24h"))).GetProperty("totals").GetProperty("runs").GetInt32());
    }
}
