using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Through the real API and Postgres: a workspace's own skills and knowledge are managed with
/// ?workspace=, kept apart from the organization's, visible to that workspace's agents on top of the
/// organization's, and never reachable from another organization. Task connections are refused for
/// plugins that don't provide tools and for another organization's task.
/// </summary>
public sealed class WorkspaceScopeApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

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
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string> WorkspaceAsync(HttpClient api, string name) =>
        (await Json(await api.PostAsJsonAsync("/api/workspaces", new { name, goal = "Prepare the quarterly board pack." })))
        .GetProperty("workspace_id").GetString()!;

    [Fact]
    public async Task A_workspaces_skills_are_its_own_and_override_the_organizations()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("ScopeSkills", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);
        var ws = await WorkspaceAsync(api, "Board");

        await Json(await api.PostAsJsonAsync("/api/skills", new { name = "reporting", description = "Org format.", instructions = "Org template." }));
        await Json(await api.PostAsJsonAsync($"/api/skills?workspace={ws}", new { name = "reporting", description = "Board format.", instructions = "Board template." }));
        await Json(await api.PostAsJsonAsync($"/api/skills?workspace={ws}", new { name = "board-prep", description = "Board packs.", instructions = "Steps." }));

        var orgSkills = await Json(await api.GetAsync("/api/skills"));
        Assert.Equal(["reporting"], orgSkills.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("Org format.", orgSkills[0].GetProperty("description").GetString());

        var wsSkills = await Json(await api.GetAsync($"/api/skills?workspace={ws}"));
        Assert.Equal(["board-prep", "reporting"], wsSkills.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(ws, wsSkills[0].GetProperty("workspace_id").GetString());
        Assert.Equal("Board template.", (await Json(await api.GetAsync($"/api/skills/reporting?workspace={ws}"))).GetProperty("instructions").GetString());

        // Deleting the workspace's version leaves the organization's.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/skills/reporting?workspace={ws}")).StatusCode);
        Assert.Equal("Org template.", (await Json(await api.GetAsync("/api/skills/reporting"))).GetProperty("instructions").GetString());

        // Another organization can't reach the workspace's skills at all.
        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("ScopeStranger", Tenancy.TenantRole.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/skills?workspace={ws}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/skills?workspace={ws}",
            new { name = "planted", description = "x", instructions = "y" })).StatusCode);
    }

    [Fact]
    public async Task A_workspaces_knowledge_is_found_by_its_agents_and_nowhere_else()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("ScopeKnowledge");
        using var api = Host.ClientFor(org);
        var ws = await WorkspaceAsync(api, "Board");
        var otherWs = await WorkspaceAsync(api, "Support");

        await Json(await api.PostAsJsonAsync("/api/memory", new { key = "refund-policy", value = "Refunds within 30 days." }));
        await Json(await api.PostAsJsonAsync($"/api/memory?workspace={ws}", new { key = "board-dates", value = "The board meets on 12 March." }));
        var form = new MultipartFormDataContent { { new ByteArrayContent("Board members: Ama, Kofi."u8.ToArray()), "files", "members.txt" } };
        Assert.Equal(1, (await Json(await api.PostAsync($"/api/memory/files?workspace={ws}", form)))[0].GetProperty("entries").GetInt32());

        string[] Keys(JsonElement e) => e.EnumerateArray().Select(r => r.GetProperty("key").GetString()!).OrderBy(k => k).ToArray();
        Assert.Equal(["refund-policy"], Keys(await Json(await api.GetAsync("/api/memory"))));
        Assert.Equal(["board-dates", "members.txt"], Keys(await Json(await api.GetAsync($"/api/memory?workspace={ws}"))));
        Assert.Empty(Keys(await Json(await api.GetAsync($"/api/memory?workspace={otherWs}"))));

        // What an agent in each place finds, through the real store and its SQL.
        var store = Host.Factory.Services.GetRequiredService<IMemoryStore>();
        string[] Found(IReadOnlyList<MemoryRecord> r) => r.Select(x => x.Key).OrderBy(k => k).ToArray();
        Assert.Equal(["board-dates", "members.txt", "refund-policy"],
            Found(await store.SearchAsync(org.TenantId, "", MemoryKind.Shared, scope: MemoryScope.ForAgentIn(ws))));
        Assert.Equal(["refund-policy"], Found(await store.SearchAsync(org.TenantId, "", MemoryKind.Shared, scope: MemoryScope.ForAgentIn(otherWs))));
        Assert.Empty(await store.SearchAsync(org.TenantId, "board", MemoryKind.Shared)); // a task agent: the organization's only
        Assert.Equal(["board-dates"], Found(await store.SearchAsync(org.TenantId, "board meets", MemoryKind.Shared, scope: MemoryScope.ForAgentIn(ws))));

        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("KnowledgeStranger"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/memory?workspace={ws}")).StatusCode);
    }

    [Fact]
    public async Task Task_connections_take_tool_providers_only_and_only_for_the_tasks_organization()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("TaskTools");
        using var api = Host.ClientFor(org);
        var taskId = (await Json(await api.PostAsJsonAsync("/api/tasks", new { goal = "Summarize our rental market data." })))
            .GetProperty("task_id").GetString()!;

        Assert.Empty((await Json(await api.GetAsync($"/api/tasks/{taskId}/connections"))).EnumerateArray());

        // A messaging channel provides no tools; an unreachable MCP server fails its check. Neither is kept.
        var slack = await api.PostAsJsonAsync($"/api/tasks/{taskId}/connections",
            new { plugin_id = "slack", name = "team", settings = new Dictionary<string, string>(), secrets = new Dictionary<string, string>() });
        Assert.Equal(HttpStatusCode.BadRequest, slack.StatusCode);
        var unreachable = await api.PostAsJsonAsync($"/api/tasks/{taskId}/connections", new
        {
            plugin_id = "mcp", name = "nowhere",
            settings = new Dictionary<string, string> { ["transport"] = "http", ["url"] = "http://127.0.0.1:9/mcp" },
            secrets = new Dictionary<string, string>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, unreachable.StatusCode);
        Assert.Empty((await Json(await api.GetAsync($"/api/tasks/{taskId}/connections"))).EnumerateArray());

        // A task can't start with a server that can't be reached: nothing is left behind.
        var bad = await api.PostAsJsonAsync("/api/tasks", new
        {
            goal = "x",
            connections = new[] { new { plugin_id = "mcp", name = "nowhere", settings = new Dictionary<string, string> { ["transport"] = "http", ["url"] = "http://127.0.0.1:9/mcp" } } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("nowhere", await bad.Content.ReadAsStringAsync());

        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("TaskToolsStranger"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/tasks/{taskId}/connections")).StatusCode);
    }
}
