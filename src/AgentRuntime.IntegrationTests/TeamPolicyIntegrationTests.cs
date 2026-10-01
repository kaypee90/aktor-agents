using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Safety;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P3, end to end: a model that tries to start a second agent for work an agent is already
/// doing is refused by the runtime, and sees a structured tool error naming the rule and the agent
/// to talk to instead. A task's own policy (here a fan-out limit) travels to its children.
/// </summary>
public sealed class TeamPolicyIntegrationTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
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

    private IAgentRegistryGrain Registry => _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0);

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static ToolCall Spawn(string role, string goal) =>
        Call("spawn_agent", new { role, goal, why_not_myself = "This needs a specialist working in parallel." });

    private static LlmCompletionResponse Respond(params ToolCall[] calls) => new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    private async Task<string> CreateRootAsync(string goal, TeamPolicy? policy)
    {
        var agentId = $"root-{Guid.NewGuid():n}"[..14];
        await Registry.RegisterAsync(new AgentDirectoryEntry
        {
            AgentId = agentId, Role = "Root Agent", Goal = goal, Status = AgentStatus.Created,
            Capabilities = ["orchestration"], Depth = 0, RootAgentId = agentId
        });

        var grain = _cluster.GrainFactory.GetGrain<IAgentGrain>(agentId);
        await grain.Initialize(new AgentInitializationRequest
        {
            AgentId = agentId, RootAgentId = agentId, Name = "Root", Role = "Root Agent", Goal = goal,
            Capabilities = ["orchestration"],
            AllowedTools = Tools.AgentToolCatalog.ResolveToolsForCapabilities(["research"]).ToList(),
            GrantedPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages,
            Budget = new ResourceBudget(),
            TaskId = Guid.NewGuid().ToString("n"),
            TeamPolicy = policy
        });
        _ = grain.Start();
        return agentId;
    }

    private static string? RoleOf(LlmCompletionRequest r) => PromptInspector.ExtractRole(r.Messages[0].Content!);

    /// <summary>The tool result a role's agent got back for its spawn_agent calls, latest first.</summary>
    private IEnumerable<JsonElement> SpawnResults(string role) => _requests
        .Where(r => RoleOf(r)?.Contains(role, StringComparison.OrdinalIgnoreCase) == true)
        .SelectMany(r => r.Messages.Where(m => m.Role == ChatRole.Tool && m.ToolName == "spawn_agent"))
        .Select(m => JsonDocument.Parse(m.Content!).RootElement.Clone());

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail("Condition was not met within the timeout.");
    }

    [Fact]
    public async Task Duplicate_role_spawn_is_rejected_with_a_structured_tool_error()
    {
        ScriptedLlmProviderRegistry.Current = r =>
        {
            _requests.Enqueue(r);
            var role = RoleOf(r) ?? string.Empty;
            // The researcher stays live (waiting for input), so the second spawn meets a live twin.
            if (!role.Contains("Root")) return new LlmCompletionResponse { Content = "Researching.", FinishReason = LlmFinishReason.Stop };

            var spawnResults = r.Messages.Count(m => m.Role == ChatRole.Tool && m.ToolName == "spawn_agent");
            return spawnResults switch
            {
                0 => Respond(Spawn("Market Research Specialist", "Research the market size for property management software")),
                // Then the classic failure: a near-identical second agent for the same work.
                1 => Respond(Spawn("market researcher agent", "Research the market size of property management software")),
                _ => Respond(Call("complete_task", new { status = "completed", summary = "done" }))
            };
        };

        var root = await CreateRootAsync("Research the AI property management SaaS market", policy: null);
        await WaitUntilAsync(async () => SpawnResults("Root").Count() >= 2 || (await Registry.GetAsync(root))?.Status == AgentStatus.Completed);

        var results = SpawnResults("Root").ToList();
        var accepted = results.First(e => e.TryGetProperty("agent_id", out _));
        var refused = results.First(e => e.TryGetProperty("error", out _));
        Assert.Equal("spawn_rejected.duplicate_role", refused.GetProperty("code").GetString());
        Assert.Equal(TeamRules.DuplicateRole, refused.GetProperty("details").GetProperty("rule").GetString());
        Assert.Equal(accepted.GetProperty("agent_id").GetString(), refused.GetProperty("details").GetProperty("existing_agent_id").GetString());
        Assert.Contains("send_message", refused.GetProperty("error").GetString());

        // Only one market researcher was ever registered.
        var team = await Registry.FindAsync(new FindAgentsQuery { RootAgentId = root });
        Assert.Single(team, a => TeamShapeValidator.SameRole(a.Role, "market researcher"));
    }

    [Fact]
    public async Task Task_fan_out_policy_is_inherited_by_children()
    {
        ScriptedLlmProviderRegistry.Current = r =>
        {
            _requests.Enqueue(r);
            var role = RoleOf(r) ?? string.Empty;
            var spawnResults = r.Messages.Count(m => m.Role == ChatRole.Tool && m.ToolName == "spawn_agent");
            if (role.Contains("Root"))
            {
                return spawnResults == 0
                    ? Respond(Spawn("Architect", "Design the system"), Spawn("Writer", "Write the proposal"), Spawn("Designer", "Design the UI mockups"))
                    : Respond(Call("complete_task", new { status = "completed", summary = "done" }));
            }

            if (role.Contains("Architect"))
            {
                return spawnResults == 0
                    ? Respond(Spawn("Database Engineer", "Design the schema"), Spawn("Security Engineer", "Threat model the API"))
                    : Respond(Call("complete_task", new { status = "completed", summary = "architecture done" }));
            }

            return Respond(Call("complete_task", new { status = "completed", summary = $"{role} done" }));
        };

        // Root may start two agents; everyone below it one.
        var root = await CreateRootAsync("Propose an architecture", new TeamPolicy { MaxFanOutByDepth = [2, 1], PreventDuplicateRoles = false });
        await WaitUntilAsync(async () => SpawnResults("Architect").Count() >= 2 && SpawnResults("Root").Count() >= 3);

        Assert.Equal(1, SpawnResults("Root").Count(e => e.TryGetProperty("code", out var c) && c.GetString() == "spawn_rejected.max_fan_out"));
        Assert.Equal(1, SpawnResults("Architect").Count(e => e.TryGetProperty("code", out var c) && c.GetString() == "spawn_rejected.max_fan_out"));

        var team = await Registry.FindAsync(new FindAgentsQuery { RootAgentId = root });
        Assert.Equal(4, team.Count); // root, 2 children, 1 grandchild
    }
}
