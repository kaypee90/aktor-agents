using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// The runtime's limits on spawning in a workspace: every spawn needs a reason, one request can
/// start only a few agents, and workers can't spawn their own.
/// </summary>
public sealed class SpawnDisciplineTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        ScriptedLlmProviderRegistry.Current = Script;
        _cluster = await DurableTestCluster.StartAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.DisposeAsync();
    }

    private IWorkspaceGrain Workspace(string id) => _cluster.Client.GetGrain<IWorkspaceGrain>(id);

    private static ToolCall Call(string name, object? args = null) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args ?? new { })
    };

    private static LlmCompletionResponse Respond(params ToolCall[] calls) =>
        new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    private static ToolCall SpawnWorker(string role) =>
        Call("spawn_agent", new { role, goal = $"{role}: do one part", why_not_myself = "This part runs in parallel with the others." });

    private static ToolCall Plan(string size, int parts) =>
        Call("plan_request", new { parts = Enumerable.Range(1, parts).Select(i => new { title = $"Part {i}", size }).ToArray() });

    /// <summary>The coordinator plans each command, then acts on the plan's result.</summary>
    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        _requests.Enqueue(r);
        var isCoordinator = r.Messages[0].Content?.Contains("acting as: Coordinator.") == true;
        if (!isCoordinator) return Respond(Call("complete_task", new { status = "completed", summary = "part done" }));

        var input = r.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var last = r.Messages[^1];
        if (last.Role == ChatRole.Tool && last.ToolName == "plan_request")
        {
            if (input.EndsWith("spawn four")) return Respond(SpawnWorker("Part A"), SpawnWorker("Part B"), SpawnWorker("Part C"), SpawnWorker("Part D"));
            if (input.EndsWith("spawn one more")) return Respond(SpawnWorker("Part E"));
            if (input.EndsWith("spawn without a reason")) return Respond(Call("spawn_agent", new { role = "Helper", goal = "Help" }));
            if (input.EndsWith("small job")) return Respond(SpawnWorker("Needless Helper"));
        }

        if (last.Role == ChatRole.Tool || !input.Contains("[Message from the user")) return Respond(Call("wait_for_events", new { summary = "Idle." }));

        if (input.EndsWith("spawn four")) return Respond(Plan("large", 4));
        if (input.EndsWith("spawn one more")) return Respond(Plan("large", 1));
        if (input.EndsWith("spawn without a reason")) return Respond(Plan("large", 2));
        if (input.EndsWith("small job")) return Respond(Plan("small", 2));
        if (input.EndsWith("spawn without a plan")) return Respond(SpawnWorker("Unplanned"));
        return Respond(Call("wait_for_events", new { summary = "Idle." }));
    }

    private async Task<string> CreateWorkspaceAsync()
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = "Shop", Goal = "Help me with my shop." });
        return id;
    }

    private async Task<WorkspaceSnapshot> WaitForAsync(string id, Func<WorkspaceSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await Workspace(id).GetSnapshot() is { } s && condition(s)) return s;
            await Task.Delay(100);
        }

        throw new TimeoutException("Workspace condition not met.");
    }

    private bool ToolResultSeen(string text) =>
        _requests.Any(r => r.Messages.Any(m => m.Role == ChatRole.Tool && m.Content?.Contains(text) == true));

    /// <summary>How many tool results containing <paramref name="text"/> the coordinator has seen.</summary>
    private int RejectionsSeen(string text) =>
        _requests.Select(r => r.Messages.Count(m => m.Role == ChatRole.Tool && m.Content?.Contains(text) == true)).DefaultIfEmpty(0).Max();

    private static int Workers(WorkspaceSnapshot s) => s.Agents.Count(a => a.Role != "Coordinator");

    [Fact]
    public async Task OneRequest_StartsAtMostThreeAgents_AndTheNextRequestGetsAFreshAllowance()
    {
        var id = await CreateWorkspaceAsync();
        await WaitForAsync(id, s => s.Agents.Any(a => a.Role == "Coordinator" && a.Status == "Waiting"));

        await Workspace(id).PostUserMessage("spawn four", null, null);
        await WaitForAsync(id, s => Workers(s) == 3 && ToolResultSeen("the most allowed"));
        Assert.True(ToolResultSeen("shared daily budget"), "the spawner should be told what the agent costs");

        await Workspace(id).PostUserMessage("spawn one more", null, null);
        var s = await WaitForAsync(id, s => Workers(s) == 4);
        Assert.DoesNotContain(s.Agents, a => a.Role == "Part D");

        // Workers do their own job: no spawn tool and no permission to spawn.
        var worker = await _cluster.Client.GetGrain<IAgentGrain>(s.Agents.First(a => a.Role == "Part A").AgentId).GetSnapshot();
        Assert.DoesNotContain("spawn_agent", worker.AllowedTools);
        Assert.False(worker.GrantedPermissions.HasFlag(ToolPermission.SpawnAgents));
        Assert.Equal(0, worker.Budget.MaxChildren);
    }

    [Fact]
    public async Task AWorker_NeedsAPlan_AndAPlanForSmallWork_AllowsNone()
    {
        var id = await CreateWorkspaceAsync();
        await WaitForAsync(id, s => s.Agents.Any(a => a.Role == "Coordinator" && a.Status == "Waiting"));

        await Workspace(id).PostUserMessage("spawn without a plan", null, null);
        await WaitForAsync(id, _ => ToolResultSeen("No workers are planned"));

        // Small parts: the plan says to do it yourself, so a worker is refused.
        await Workspace(id).PostUserMessage("small job", null, null);
        var s = await WaitForAsync(id, _ => ToolResultSeen("\"approach\":\"self\"") && RejectionsSeen("No workers are planned") >= 2);

        Assert.Equal(0, Workers(s));
    }

    [Fact]
    public async Task ASpawnWithoutAReason_IsRejected()
    {
        var id = await CreateWorkspaceAsync();
        await WaitForAsync(id, s => s.Agents.Any(a => a.Role == "Coordinator" && a.Status == "Waiting"));

        await Workspace(id).PostUserMessage("spawn without a reason", null, null);
        var s = await WaitForAsync(id, _ => ToolResultSeen("spawn_agent needs why_not_myself"));

        Assert.Equal(0, Workers(s));
    }
}
