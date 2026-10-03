using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure;
using AgentRuntime.Memory;
using AgentRuntime.Skills;
using AgentRuntime.Tools;

namespace AgentRuntime.Tests;

/// <summary>
/// Skills and knowledge a workspace keeps for itself: its agents see them on top of the
/// organization's (a workspace skill wins over an organization skill of the same name); agents of
/// other workspaces and of tasks never see them.
/// </summary>
public sealed class WorkspaceScopeTests
{
    private const string Tenant = "org-scope";
    private const string Ws = "ws-0000000001";
    private const string OtherWs = "ws-0000000002";

    private static ToolExecutionRequest Request(string tool, string taskId, object args) => new()
    {
        ToolName = tool,
        AgentId = "agent-1",
        TaskId = taskId,
        TenantId = Tenant,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static async Task<InMemorySkillStore> SkillsAsync()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(Tenant, SkillPackage.FromParts("reporting", "The organization's report format.", "Use the org template.", null));
        await store.SaveAsync(Tenant, SkillPackage.FromParts("reporting", "This workspace's report format.", "Use the board template.", null), Ws);
        await store.SaveAsync(Tenant, SkillPackage.FromParts("board-prep", "Preparing board packs.", "Steps.", null), Ws);
        return store;
    }

    [Fact]
    public async Task Workspace_agents_see_their_skills_on_top_of_the_organizations_others_see_only_the_organizations()
    {
        var store = await SkillsAsync();

        var inWorkspace = await store.ListForAgentAsync(Tenant, Ws);
        Assert.Equal(["board-prep", "reporting"], inWorkspace.Select(s => s.Name));
        Assert.Equal("This workspace's report format.", inWorkspace.Single(s => s.Name == "reporting").Description);

        Assert.Equal(["reporting"], (await store.ListForAgentAsync(Tenant, OtherWs)).Select(s => s.Name));
        Assert.Equal("The organization's report format.", (await store.ListForAgentAsync(Tenant, null)).Single().Description);

        // Each scope lists only its own skills.
        Assert.Equal(["board-prep", "reporting"], (await store.ListAsync(Tenant, enabledOnly: false, Ws)).Select(s => s.Name));
        Assert.Equal(["reporting"], (await store.ListAsync(Tenant, enabledOnly: false)).Select(s => s.Name));
    }

    [Fact]
    public async Task Load_skill_resolves_by_the_agents_workspace_and_falls_back_to_the_organization()
    {
        var store = await SkillsAsync();
        var load = new LoadSkillTool(store);

        var own = await load.ExecuteAsync(Request("load_skill", Ws, new { name = "reporting" }));
        Assert.Contains("board template", own.ResultJson);

        var org = await load.ExecuteAsync(Request("load_skill", "task-abc", new { name = "reporting" }));
        Assert.Contains("org template", org.ResultJson);

        // Another workspace and a task can't load this workspace's own skill.
        Assert.False((await load.ExecuteAsync(Request("load_skill", OtherWs, new { name = "board-prep" }))).Success);
        Assert.False((await load.ExecuteAsync(Request("load_skill", "task-abc", new { name = "board-prep" }))).Success);

        // Switched off in the workspace: its agents fall back to the organization's version.
        await store.SetEnabledAsync(Tenant, "reporting", false, Ws);
        Assert.Contains("org template", (await load.ExecuteAsync(Request("load_skill", Ws, new { name = "reporting" }))).ResultJson);
    }

    [Fact]
    public async Task Knowledge_written_in_a_workspace_stays_there_unless_shared_with_the_organization()
    {
        var memory = new InMemoryMemoryStore();
        var write = new WriteMemoryTool(memory);
        var search = new SearchKnowledgeTool(memory);
        var read = new ReadMemoryTool(memory);

        await memory.WriteAsync(new MemoryRecord { TenantId = Tenant, AgentId = "user", Kind = MemoryKind.Shared, Key = "org-policy", Value = "refunds within 30 days" });
        await write.ExecuteAsync(Request("write_memory", Ws, new { key = "board-dates", value = "board meets in March", shared = true }));
        await write.ExecuteAsync(Request("write_memory", Ws, new { key = "brand", value = "navy and gold", shared = true, organization_wide = true }));

        async Task<string[]> Found(string taskId)
        {
            var result = await search.ExecuteAsync(Request("search_knowledge", taskId, new { query = "" }));
            return JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("results").EnumerateArray()
                .Select(r => r.GetProperty("key").GetString()!).OrderBy(k => k).ToArray();
        }

        Assert.Equal(["board-dates", "brand", "org-policy"], await Found(Ws));
        Assert.Equal(["brand", "org-policy"], await Found(OtherWs));
        Assert.Equal(["brand", "org-policy"], await Found("task-abc"));

        // read_memory of a shared key follows the same rule.
        var other = await read.ExecuteAsync(Request("read_memory", OtherWs, new { key = "board-dates" }) with { AgentId = "agent-2" });
        Assert.Contains("\"found\":false", other.ResultJson);
        var own = await read.ExecuteAsync(Request("read_memory", Ws, new { key = "board-dates" }) with { AgentId = "agent-3" });
        Assert.Contains("board meets in March", own.ResultJson);

        // People managing one workspace's knowledge see just that workspace's.
        Assert.Equal(["board-dates"], (await memory.SearchAsync(Tenant, "", MemoryKind.Shared, scope: MemoryScope.OnlyWorkspace(Ws))).Select(r => r.Key));
    }
}
