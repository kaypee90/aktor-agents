using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Tools;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Agents running out of budget or time: they are warned, get one last step to report, and if
/// they don't the runtime reports for them. Their parent always gets a result with the remaining
/// work, never a bare failure.
/// </summary>
public sealed class BudgetWrapUpTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();
    private readonly ConcurrentQueue<string> _parentNotices = new();

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        _cluster = await DurableTestCluster.StartAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.DisposeAsync();
    }

    private IAgentRegistryGrain Registry => _cluster.Client.GetGrain<IAgentRegistryGrain>(0);

    private static ToolCall Call(string name, object? args = null) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args ?? new { })
    };

    private static LlmCompletionResponse Respond(int tokens, params ToolCall[] calls) =>
        new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls, InputTokens = tokens };

    private static LlmCompletionResponse Say(string text) => new() { Content = text, FinishReason = LlmFinishReason.Stop };

    private static string LastInput(LlmCompletionRequest r) =>
        r.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;

    private static bool IsFinalStep(LlmCompletionRequest r) => LastInput(r).Contains("This is your final step");

    /// <summary>A root that delegates one big job to a worker, then reports whatever comes back.
    /// The worker keeps working (each step costing <paramref name="workerTokensPerCall"/>) and only
    /// reports at its final step if <paramref name="workerReportsWhenTold"/>.</summary>
    private Func<LlmCompletionRequest, LlmCompletionResponse> RootAndWorker(int workerTokensPerCall, bool workerReportsWhenTold)
    {
        var step = 0;
        return r =>
        {
            _requests.Enqueue(r);
            var role = PromptInspector.ExtractRole(r.Messages[0].Content!);
            var input = LastInput(r);

            if (role == "Root Agent")
            {
                if (input.Contains("Your child agent"))
                {
                    _parentNotices.Enqueue(input);
                    return Respond(0, Call("complete_task", new { status = "completed", summary = "root done" }));
                }

                var spawned = r.Messages.Any(m => m.ToolCalls?.Any(t => t.Name == "spawn_agent") == true);
                return spawned
                    ? Say("Waiting for the researcher.")
                    : Respond(0, Call("spawn_agent", new { role = "Researcher", goal = "Research everything about the market", why_not_myself = "Big independent job." }));
            }

            if (IsFinalStep(r) && workerReportsWhenTold)
            {
                return Respond(workerTokensPerCall, Call("complete_task", new
                {
                    status = "partial",
                    summary = "Covered pricing; ran out before competitors.",
                    remaining_work = new[] { "Research competitors" }
                }));
            }

            return Respond(workerTokensPerCall, Call("find_agents", new { capabilities = new[] { $"topic-{Interlocked.Increment(ref step)}" } }));
        };
    }

    private async Task<string> StartRootAsync(ResourceBudget budget)
    {
        var agentId = $"root-{Guid.NewGuid():n}"[..14];
        await Registry.RegisterAsync(new AgentDirectoryEntry { AgentId = agentId, Role = "Root Agent", Goal = "Market analysis", RootAgentId = agentId });
        await _cluster.Client.GetGrain<IAgentGrain>(agentId).Initialize(new AgentInitializationRequest
        {
            AgentId = agentId,
            RootAgentId = agentId,
            Name = "Root",
            Role = "Root Agent",
            Goal = "Market analysis",
            AllowedTools = ["spawn_agent", "find_agents", "complete_task"],
            GrantedPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages,
            Budget = budget,
            TaskId = $"task-{Guid.NewGuid():n}",
            AutoStart = true
        });
        return agentId;
    }

    private async Task<AgentDirectoryEntry> WaitForStatusAsync(string agentId, AgentStatus status, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var entry = await Registry.GetAsync(agentId);
            if (entry?.Status == status) return entry;
            if (entry?.Status is AgentStatus.Failed or AgentStatus.TimedOut)
            {
                Assert.Fail($"Agent {agentId} ended {entry.Status}: {(await _cluster.Client.GetGrain<IAgentGrain>(agentId).GetSnapshot()).FailureReason}");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Agent {agentId} never reached {status} (now {(await Registry.GetAsync(agentId))?.Status}).");
    }

    private async Task<string> WorkerOfAsync(string rootId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Registry.GetChildrenAsync(rootId)) is [var child, ..]) return child;
            await Task.Delay(50);
        }

        throw new TimeoutException("The root never spawned its worker.");
    }

    [Fact]
    public async Task RunningOutOfTokens_WarnsTheAgent_ThenItReportsAPartialResult_InsteadOfFailing()
    {
        ScriptedLlmProviderRegistry.Current = RootAndWorker(workerTokensPerCall: 4_000, workerReportsWhenTold: true);
        // The worker gets half the root's tokens (30,000): about six steps at 4,000 each.
        var rootId = await StartRootAsync(new ResourceBudget { MaxTokens = 60_000, MaxToolCalls = 100, MaxChildren = 1, MaxCostUsd = 100m, MaxDurationSeconds = 600 });
        var workerId = await WorkerOfAsync(rootId);

        await WaitForStatusAsync(workerId, AgentStatus.Completed);
        await WaitForStatusAsync(rootId, AgentStatus.Completed);

        var worker = await _cluster.Client.GetGrain<IAgentGrain>(workerId).GetSnapshot();
        Assert.True(worker.Usage.TokensUsed <= worker.Budget.MaxTokens, $"used {worker.Usage.TokensUsed} of {worker.Budget.MaxTokens}");

        var workerCalls = _requests.Where(r => PromptInspector.ExtractRole(r.Messages[0].Content!) == "Researcher").ToList();
        var warned = workerCalls.FindIndex(r => LastInput(r).Contains("Start finishing"));
        var final = workerCalls.FindIndex(IsFinalStep);
        Assert.True(warned >= 0 && final > warned, $"warned at {warned}, final step at {final}");
        // The final step offers only the report.
        Assert.Equal(["complete_task"], workerCalls[final].Tools.Select(t => t.Name));

        var notice = Assert.Single(_parentNotices);
        Assert.Contains("completed (partial)", notice);
        Assert.Contains("Research competitors", notice);
        Assert.Contains("stopped before finishing", notice);
    }

    [Fact]
    public async Task AnAgentThatIgnoresItsFinalStep_IsReportedByTheRuntime_WithItsRemainingWork()
    {
        ScriptedLlmProviderRegistry.Current = RootAndWorker(workerTokensPerCall: 4_000, workerReportsWhenTold: false);
        var rootId = await StartRootAsync(new ResourceBudget { MaxTokens = 60_000, MaxToolCalls = 100, MaxChildren = 1, MaxCostUsd = 100m, MaxDurationSeconds = 600 });
        var workerId = await WorkerOfAsync(rootId);

        await WaitForStatusAsync(workerId, AgentStatus.Completed);
        await WaitForStatusAsync(rootId, AgentStatus.Completed);

        var worker = await _cluster.Client.GetGrain<IAgentGrain>(workerId).GetSnapshot();
        Assert.True(worker.Usage.TokensUsed <= worker.Budget.MaxTokens, $"used {worker.Usage.TokensUsed} of {worker.Budget.MaxTokens}");

        // Its final step tried to keep working: that call was refused and no further call was made.
        Assert.DoesNotContain(_requests, r => r.Messages.Any(m => m.Role == ChatRole.Tool && m.Content?.Contains("only complete_task can run now") == true));
        var notice = Assert.Single(_parentNotices);
        Assert.Contains("completed (partial)", notice);
        Assert.Contains("Stopped by the runtime", notice);
        Assert.Contains("Finish the goal: Research everything about the market", notice);
    }

    [Fact]
    public async Task RunningOutOfToolCalls_StillLetsTheAgentReport()
    {
        ScriptedLlmProviderRegistry.Current = RootAndWorker(workerTokensPerCall: 0, workerReportsWhenTold: true);
        // The worker gets half the root's tool calls: 4.
        var rootId = await StartRootAsync(new ResourceBudget { MaxTokens = 1_000_000, MaxToolCalls = 8, MaxChildren = 1, MaxCostUsd = 100m, MaxDurationSeconds = 600 });
        var workerId = await WorkerOfAsync(rootId);

        await WaitForStatusAsync(workerId, AgentStatus.Completed);
        var notice = await WaitForNoticeAsync();
        Assert.Contains("completed (partial)", notice);
        Assert.Contains("Research competitors", notice);
    }

    [Fact]
    public async Task AnAgentParkedPastItsDeadline_IsWokenAndReports_InsteadOfWaitingForever()
    {
        ScriptedLlmProviderRegistry.Current = r =>
        {
            _requests.Enqueue(r);
            // Never uses a tool until told it's out of time, so it parks waiting for input.
            return IsFinalStep(r)
                ? Respond(0, Call("complete_task", new { status = "partial", summary = "Out of time.", remaining_work = new[] { "Everything" } }))
                : Say("Thinking about it.");
        };

        var agentId = $"root-{Guid.NewGuid():n}"[..14];
        await Registry.RegisterAsync(new AgentDirectoryEntry { AgentId = agentId, Role = "Root Agent", Goal = "Ponder", RootAgentId = agentId });
        await _cluster.Client.GetGrain<IAgentGrain>(agentId).Initialize(new AgentInitializationRequest
        {
            AgentId = agentId,
            RootAgentId = agentId,
            Name = "Root",
            Role = "Root Agent",
            Goal = "Ponder",
            AllowedTools = ["find_agents", "complete_task"],
            Budget = new ResourceBudget { MaxTokens = 1_000_000, MaxToolCalls = 100, MaxCostUsd = 100m, MaxDurationSeconds = 2 },
            TaskId = $"task-{Guid.NewGuid():n}",
            AutoStart = true
        });

        await WaitForStatusAsync(agentId, AgentStatus.Waiting);
        await WaitForStatusAsync(agentId, AgentStatus.Completed);
        Assert.Contains(_requests, r => IsFinalStep(r) && LastInput(r).Contains("time budget"));
    }

    private async Task<string> WaitForNoticeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (_parentNotices.TryPeek(out var notice)) return notice;
            await Task.Delay(100);
        }

        throw new TimeoutException("The parent was never told.");
    }
}
