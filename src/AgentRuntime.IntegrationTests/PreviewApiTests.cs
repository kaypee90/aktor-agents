using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P2 through the real API: a preview plans the team (here the mock provider's, which
/// matches what it then builds), a task started from it reports estimate vs actual, the next
/// preview is calibrated on that run, and the per-branch spend adds up.
/// </summary>
public sealed class PreviewApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private const string DemoGoal = "Research the feasibility of building an AI-powered property management SaaS.";

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Preview_plans_the_team_and_the_run_reports_estimate_vs_actual()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Preview");
        using var api = Host.ClientFor(org);

        var preview = await Json(await api.PostAsJsonAsync("/api/tasks/preview", new { goal = DemoGoal }));
        output.WriteLine(preview.ToString());
        Assert.Equal(5, preview.GetProperty("team_size").GetInt32());
        Assert.Equal(2, preview.GetProperty("max_depth").GetInt32());
        Assert.Equal("research", preview.GetProperty("goal_type").GetString());
        Assert.Equal("defaults", preview.GetProperty("calibration").GetProperty("source").GetString());
        var estimate = preview.GetProperty("estimate");
        Assert.True(estimate.GetProperty("cost_usd_low").GetDecimal() < estimate.GetProperty("cost_usd_high").GetDecimal());
        Assert.False(preview.GetProperty("requires_confirmation").GetBoolean());
        Assert.True(preview.GetProperty("planning").GetProperty("tokens").GetInt32() > 0);

        var started = await Json(await api.PostAsJsonAsync("/api/tasks", new { goal = DemoGoal, preview_id = preview.GetProperty("preview_id").GetString() }));
        var taskId = started.GetProperty("task_id").GetString()!;
        var done = await Json(await api.GetAsync($"/api/tasks/{taskId}/wait?timeout_seconds=90"));
        Assert.True(done.GetProperty("done").GetBoolean());

        // Spend by branch: the root's branch is everyone's spend together.
        var spend = await Json(await api.GetAsync($"/api/tasks/{taskId}/spend"));
        var agents = spend.EnumerateArray().ToList();
        var root = agents.Single(a => a.GetProperty("parent_agent_id").ValueKind == JsonValueKind.Null);
        Assert.Equal(agents.Sum(a => a.GetProperty("tokens_used").GetInt64()), root.GetProperty("branch_tokens").GetInt64());
        Assert.All(agents, a => Assert.True(a.GetProperty("branch_cost_usd").GetDecimal() <= a.GetProperty("budget_max_cost_usd").GetDecimal()));

        var metrics = (await Json(await api.GetAsync($"/api/tasks/{taskId}/result"))).GetProperty("result").GetProperty("metrics");
        output.WriteLine(metrics.ToString());
        Assert.Equal("5", metrics.GetProperty("estimated_team_size").GetString());
        Assert.True(metrics.TryGetProperty("estimated_cost_usd", out _));
        Assert.True(metrics.TryGetProperty("cost_estimate_ratio", out _));
        Assert.True(int.Parse(metrics.GetProperty("actual_team_size").GetString()!) >= 1);

        // The next preview learns from that run.
        var calibrated = await Json(await api.PostAsJsonAsync("/api/tasks/preview", new { goal = DemoGoal }));
        Assert.Equal("history", calibrated.GetProperty("calibration").GetProperty("source").GetString());
        Assert.Equal(1, calibrated.GetProperty("calibration").GetProperty("samples").GetInt32());
        Assert.NotEqual(estimate.GetProperty("tokens_expected").GetInt64(), calibrated.GetProperty("estimate").GetProperty("tokens_expected").GetInt64());
    }

    [Fact]
    public async Task Budget_caps_the_estimate_and_a_costly_preview_requires_confirmation()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("PreviewCap");
        using var api = Host.ClientFor(org);

        var capped = await Json(await api.PostAsJsonAsync("/api/tasks/preview", new { goal = DemoGoal, budget = new { max_cost_usd = 0.10, max_tokens = 1_000_000 } }));
        Assert.True(capped.GetProperty("capped_by_budget").GetBoolean());
        Assert.Equal(0.10m, capped.GetProperty("estimate").GetProperty("cost_usd_high").GetDecimal());

        var big = await Json(await api.PostAsJsonAsync("/api/tasks/preview", new { goal = DemoGoal, budget = new { max_cost_usd = 40, max_tokens = 5_000_000, max_children = 5 } }));
        // 5 agents × 40k tokens × 2 at the default prices stays under $2: no confirmation needed.
        Assert.False(big.GetProperty("requires_confirmation").GetBoolean());
    }

    [Fact]
    public async Task Another_organizations_preview_is_not_claimed()
    {
        if (Skip()) return;
        var mine = await Host.CreateOrganizationAsync("PreviewMine");
        var theirs = await Host.CreateOrganizationAsync("PreviewTheirs");
        using var myApi = Host.ClientFor(mine);
        using var theirApi = Host.ClientFor(theirs);

        var preview = await Json(await theirApi.PostAsJsonAsync("/api/tasks/preview", new { goal = DemoGoal }));
        var started = await Json(await myApi.PostAsJsonAsync("/api/tasks", new { goal = DemoGoal, preview_id = preview.GetProperty("preview_id").GetString() }));
        var task = await Json(await myApi.GetAsync($"/api/tasks/{started.GetProperty("task_id").GetString()}/wait?timeout_seconds=90"));
        var result = await Json(await myApi.GetAsync($"/api/tasks/{task.GetProperty("task_id").GetString()}/result"));
        Assert.False(result.GetProperty("result").GetProperty("metrics").TryGetProperty("estimated_cost_usd", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await theirApi.GetAsync($"/api/tasks/{task.GetProperty("task_id").GetString()}/spend")).StatusCode);
    }
}
