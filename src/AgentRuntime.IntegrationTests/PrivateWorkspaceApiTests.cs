using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Tenancy;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Private workspaces through the real API and Postgres (docs/workspaces.md#private-workspaces): to
/// everyone but the owner, including the organization's admins, a private workspace, its runs,
/// events, agents and spend don't exist. Its owner can run it fully, even as a plain member, and
/// make it visible to the organization again.
/// </summary>
public sealed class PrivateWorkspaceApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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

    private static object Pipeline() => new
    {
        stages = new[] { new { stage_id = "do", name = "Do it", role = "Worker", instructions = "Do what the input asks.", inputs = Array.Empty<string>(), retries = 0 } }
    };

    private static async Task<string> CreateAsync(HttpClient api, string name, string visibility)
    {
        var created = await Json(await api.PostAsJsonAsync("/api/workspaces", new { name, goal = "Handle requests.", pipeline = Pipeline(), visibility }));
        return created.GetProperty("workspace_id").GetString()!;
    }

    private static async Task<bool> ListsAsync(HttpClient api, string workspaceId) =>
        (await Json(await api.GetAsync("/api/workspaces"))).EnumerateArray().Any(w => w.GetProperty("workspace_id").GetString() == workspaceId);

    [Fact]
    public async Task Only_the_owner_sees_a_private_workspace_its_runs_events_agents_and_spend()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Privacy", TenantRole.Admin);
        using var owner = Host.ClientFor(org);
        using var colleague = Host.ClientFor(await Host.AddMemberAsync(org, "Colleague", TenantRole.Member));
        using var admin = Host.ClientFor(await Host.AddMemberAsync(org, "Admin", TenantRole.Admin));

        var id = await CreateAsync(owner, "My drafts", "Private");
        var snapshot = await Json(await owner.GetAsync($"/api/workspaces/{id}"));
        Assert.Equal("Private", snapshot.GetProperty("visibility").GetString());
        Assert.True(await ListsAsync(owner, id));

        var run = await Json(await owner.PostAsJsonAsync($"/api/workspaces/{id}/runs", new { input = "Summarise my week" }));
        var runId = run.GetProperty("run_id").GetString()!;
        for (var i = 0; i < 60 && (await owner.GetAsync($"/api/tasks/{runId}")).StatusCode != HttpStatusCode.OK; i++) await Task.Delay(250);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/tasks/{runId}")).StatusCode);
        await Json(await owner.GetAsync($"/api/tasks/{runId}/wait?timeout_seconds=60"));

        // The owner's own view has the run's events.
        JsonElement ownerEvents = default;
        for (var i = 0; i < 40; i++)
        {
            ownerEvents = await Json(await owner.GetAsync("/api/events?limit=1000"));
            if (ownerEvents.EnumerateArray().Any(e => e.GetProperty("task_id").GetString() == runId)) break;
            await Task.Delay(250);
        }

        Assert.Contains(ownerEvents.EnumerateArray(), e => e.GetProperty("task_id").GetString() == runId);

        foreach (var other in new[] { colleague, admin })
        {
            Assert.False(await ListsAsync(other, id));
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/workspaces/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/workspaces/{id}/pipeline")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/workspaces/{id}/runs", new { input = "peek" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/tasks/{runId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/tasks/{runId}/result")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/events?taskId={id}")).StatusCode);
            Assert.DoesNotContain((await Json(await other.GetAsync("/api/tasks"))).EnumerateArray(), t => t.GetProperty("task_id").GetString() == runId);
            Assert.DoesNotContain((await Json(await other.GetAsync("/api/events?limit=1000"))).EnumerateArray(),
                e => e.GetProperty("task_id").GetString() is { } t && (t == runId || t == id));
            Assert.DoesNotContain((await Json(await other.GetAsync("/api/agents"))).EnumerateArray(), a => a.GetProperty("root_agent_id").GetString() == runId);
            var analytics = await Json(await other.GetAsync("/api/analytics?scope=workspaces&range=24h"));
            Assert.DoesNotContain(analytics.GetProperty("by_workspace").EnumerateArray(), w => w.GetProperty("workspace_id").GetString() == id);
            // Not even the visibility can be changed by someone who can't see it.
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/workspaces/{id}/visibility", new { visibility = "Organization" })).StatusCode);
        }

        // A replay would be an ordinary task, visible to everyone: refused.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/tasks/{runId}/replay", new { mode = "full" })).StatusCode);

        // A copy stays private.
        var copy = await Json(await owner.PostAsJsonAsync($"/api/workspaces/{id}/clone", new { name = "My drafts 2" }));
        var copyId = copy.GetProperty("workspace_id").GetString()!;
        Assert.Equal("Private", (await Json(await owner.GetAsync($"/api/workspaces/{copyId}"))).GetProperty("visibility").GetString());
        Assert.False(await ListsAsync(colleague, copyId));

        // Shared with the organization: everyone sees it and its runs.
        await Json(await owner.PutAsJsonAsync($"/api/workspaces/{id}/visibility", new { visibility = "Organization" }));
        Assert.True(await ListsAsync(colleague, id));
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync($"/api/workspaces/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync($"/api/tasks/{runId}")).StatusCode);
        // ...but only its owner decides who sees it.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/workspaces/{id}/visibility", new { visibility = "Private" })).StatusCode);
    }

    [Fact]
    public async Task A_member_runs_their_private_workspace_fully_but_not_a_shared_one()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("PrivacyOwner", TenantRole.Admin);
        using var member = Host.ClientFor(await Host.AddMemberAsync(org, "Member", TenantRole.Member));

        // Nobody else can see a private workspace, so its owner has an admin's say over it.
        var mine = await CreateAsync(member, "Mine", "Private");
        Assert.Equal(HttpStatusCode.NoContent, (await member.PutAsJsonAsync($"/api/workspaces/{mine}/budget", new { daily_cost_limit_usd = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsync($"/api/workspaces/{mine}/archive", null)).StatusCode);

        // A shared workspace keeps the organization's roles.
        var shared = await CreateAsync(member, "Shared", "Organization");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/workspaces/{shared}/budget", new { daily_cost_limit_usd = 5 })).StatusCode);
    }
}
