using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>A task's fan-out per level sets its budget's max_children unless the caller set one
/// (docs/safety.md), so the dashboard needs only the fan-out.</summary>
public sealed class FanOutBudgetApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
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

    private static async Task<int> MaxChildrenAsync(HttpClient api, object request)
    {
        var started = await Json(await api.PostAsJsonAsync("/api/tasks", request));
        var task = await Json(await api.GetAsync($"/api/tasks/{started.GetProperty("task_id").GetString()}"));
        return task.GetProperty("budget").GetProperty("max_children").GetInt32();
    }

    [Fact]
    public async Task Fan_out_sets_max_children_unless_the_caller_set_it()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("FanOut"));

        // The largest entry, so no level is held below what the fan-out allows.
        Assert.Equal(3, await MaxChildrenAsync(api, new { goal = "Summarise the quarter.", team_policy = new { max_fan_out_by_depth = new[] { 3, 0 } } }));
        Assert.Equal(4, await MaxChildrenAsync(api, new { goal = "Summarise the quarter.", team_policy = new { max_fan_out_by_depth = new[] { 2, 4 } } }));

        // A max_children of the caller's own is kept.
        Assert.Equal(1, await MaxChildrenAsync(api, new
        {
            goal = "Summarise the quarter.",
            budget = new { max_children = 1 },
            team_policy = new { max_fan_out_by_depth = new[] { 3, 0 } }
        }));
    }
}
