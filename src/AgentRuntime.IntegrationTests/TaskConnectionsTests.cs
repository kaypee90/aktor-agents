using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.Integrations;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// A task's own tool connections (an MCP server or API the task's owner connects): the root agent
/// and every agent it starts see the enabled tools and can call them, with the secret supplied by
/// the runtime. Another task's agents don't see them, and another organization can't add to them.
/// </summary>
public sealed class TaskConnectionsTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        FakeCrmPlugin.Reset();
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static LlmCompletionResponse Respond(ToolCall call) => new() { ToolCalls = [call], FinishReason = LlmFinishReason.ToolCalls };

    private static ConnectionRequest Crm(string apiKey) => new()
    {
        PluginId = "fake-crm",
        Name = "crm",
        Settings = new Dictionary<string, string> { ["region"] = "west-africa" },
        Secrets = new Dictionary<string, string> { ["api_key"] = apiKey }
    };

    [Fact]
    public async Task The_root_and_the_agents_it_starts_use_the_tasks_connection_tools()
    {
        const string tenant = "org-taskconn";
        var taskId = Guid.NewGuid().ToString("n");
        var connections = _cluster.GrainFactory.GetGrain<ITaskConnectionsGrain>(taskId);

        Assert.False((await connections.AddConnection(tenant, Crm("bad"))).Success); // validated before it's kept
        var added = await connections.AddConnection(tenant, Crm("key-123"));
        Assert.True(added.Success, added.Message);
        Assert.Contains(added.Connection!.Tools, t => t.Name == "crm__lookup_customer");
        // Not this organization's task: refused.
        Assert.False((await connections.AddConnection("org-someone-else", Crm("key-x") with { Name = "other" })).Success);

        var offered = new ConcurrentDictionary<string, bool>();
        ScriptedLlmProviderRegistry.Current = r =>
        {
            var role = PromptInspector.ExtractRole(r.Messages[0].Content!);
            offered[role] = r.Tools.Any(t => t.Name == "crm__lookup_customer");
            var history = r.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content ?? "").ToList();

            if (!role.Contains("Root", StringComparison.OrdinalIgnoreCase))
            {
                return history.Any(h => h.Contains("Ama Mensah"))
                    ? Respond(Call("complete_task", new { status = "completed", summary = "Customer C-7 is Ama Mensah (gold)." }))
                    : Respond(Call("crm__lookup_customer", new { id = "C-7" }));
            }

            return r.Messages.Any(m => m.Content?.Contains("completed") == true && m.Role == ChatRole.User && m.Content.Contains("child"))
                ? Respond(Call("complete_task", new { status = "completed", summary = "Looked up the customer." }))
                : r.Messages.Any(m => m.ToolCalls?.Any(t => t.Name == "spawn_agent") == true)
                ? new LlmCompletionResponse { Content = "Waiting for the analyst.", FinishReason = LlmFinishReason.Stop }
                : Respond(Call("spawn_agent", new { role = "Account Analyst", goal = "Look up customer C-7 in the CRM", why_not_myself = "Runs alongside the rest." }));
        };

        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        var root = await StartRootAsync(tenant, taskId, rootId, ToolPermission.SpawnAgents | ToolPermission.SendMessages | ToolPermission.Integrations);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && (await root.GetSnapshot()).Status != AgentStatus.Completed) await Task.Delay(50);
        Assert.Equal(AgentStatus.Completed, (await root.GetSnapshot()).Status);

        Assert.True(offered.Single(o => o.Key.Contains("Root", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.True(offered.Single(o => o.Key.Contains("Analyst", StringComparison.OrdinalIgnoreCase)).Value);
        // The child called the tool; the plugin got the secret, which no agent ever saw.
        var call = Assert.Single(FakeCrmPlugin.ToolCalls);
        Assert.Equal(("lookup_customer", "key-123"), (call.Tool, call.ApiKey));
        Assert.Contains("C-7", call.Args);
    }

    [Fact]
    public async Task Another_tasks_agents_and_agents_without_the_permission_dont_see_them()
    {
        const string tenant = "org-taskconn2";
        var taskId = Guid.NewGuid().ToString("n");
        Assert.True((await _cluster.GrainFactory.GetGrain<ITaskConnectionsGrain>(taskId).AddConnection(tenant, Crm("key-1"))).Success);

        var offered = new ConcurrentBag<bool>();
        ScriptedLlmProviderRegistry.Current = r =>
        {
            offered.Add(r.Tools.Any(t => t.Name.StartsWith("crm__", StringComparison.Ordinal)));
            return Respond(Call("complete_task", new { status = "completed", summary = "ok" }));
        };

        // A root of a different task, and a root of this task without Integrations.
        var other = await StartRootAsync(tenant, Guid.NewGuid().ToString("n"), $"root-{Guid.NewGuid():n}"[..14], ToolPermission.Integrations);
        var noPermission = await StartRootAsync(tenant, taskId, $"root-{Guid.NewGuid():n}"[..14], ToolPermission.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && offered.Count < 2) await Task.Delay(50);

        Assert.Equal(2, offered.Count);
        Assert.All(offered, Assert.False);
        _ = (other, noPermission);
    }

    private async Task<IAgentGrain> StartRootAsync(string tenant, string taskId, string rootId, ToolPermission permissions)
    {
        await _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0).RegisterAsync(new AgentDirectoryEntry
        {
            AgentId = rootId, Role = "Root Agent", Goal = "g", RootAgentId = rootId, TenantId = tenant
        });
        var root = _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId);
        await root.Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = "Find out who customer C-7 is.",
            AllowedTools = ["spawn_agent", "complete_task"],
            GrantedPermissions = permissions,
            Budget = new ResourceBudget(),
            TaskId = taskId,
            TenantId = tenant,
            AutoStart = true
        });
        return root;
    }
}
