using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.Durability;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P6: a finished run replays from its step journal into the same agent tree, messages and
/// result, without calling the model or re-running a non-idempotent tool; a fork replays up to a
/// step and continues live from there.
/// </summary>
public sealed class ReplayTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private int _liveCalls;

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        builder.AddSiloBuilderConfigurator<ChargeToolConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private sealed class ChargeToolConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.ConfigureServices(s => s.AddSingleton<Tools.ITool, ChargeCardTestTool>());
    }

    private IAgentRegistryGrain Registry => _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0);

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..10],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static LlmCompletionResponse Respond(params ToolCall[] calls) =>
        new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls, InputTokens = 800, OutputTokens = 60 };

    /// <summary>
    /// The original run: the root pays for a data set (a non-idempotent tool) and starts a buyer and
    /// an analyst; both report; the root waits for both reports, then summarizes what it heard.
    /// </summary>
    private LlmCompletionResponse Original(LlmCompletionRequest r, string summaryPrefix = "Summary")
    {
        Interlocked.Increment(ref _liveCalls);
        var role = PromptInspector.ExtractRole(r.Messages[0].Content!) ?? string.Empty;
        var toolResults = r.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolName).ToList();
        var reports = r.Messages.Count(m => m.Role == ChatRole.User && m.Content?.Contains("CompletionNotification") == true);

        if (role.Contains("Root"))
        {
            if (!toolResults.Contains("spawn_agent"))
            {
                return Respond(
                    Call("test_charge_card", new { amount = 42 }),
                    Call("spawn_agent", new { role = "Buyer", goal = "Check the data set we bought", why_not_myself = "Runs in parallel with the analysis." }),
                    Call("spawn_agent", new { role = "Analyst", goal = "Analyze the market", why_not_myself = "Runs in parallel with the check." }));
            }

            if (reports < 2) return new LlmCompletionResponse { Content = "Waiting for both reports.", FinishReason = LlmFinishReason.Stop };
            return Respond(Call("complete_task", new { status = "completed", summary = $"{summaryPrefix}: bought the data and analyzed the market." }));
        }

        return Respond(Call("complete_task", new { status = "completed", summary = role.Contains("Buyer") ? "The data set is complete." : "The market is growing." }));
    }

    private async Task<string> StartRootAsync(string taskId, ReplaySpec? replay)
    {
        var agentId = $"root-{Guid.NewGuid():n}"[..14];
        await Registry.RegisterAsync(new AgentDirectoryEntry
        {
            AgentId = agentId, Role = "Root Agent", Goal = "Buy and analyze market data", Status = AgentStatus.Created,
            Capabilities = ["orchestration"], RootAgentId = agentId
        });
        var grain = _cluster.GrainFactory.GetGrain<IAgentGrain>(agentId);
        await grain.Initialize(new AgentInitializationRequest
        {
            AgentId = agentId, RootAgentId = agentId, Name = "Root", Role = "Root Agent", Goal = "Buy and analyze market data",
            Capabilities = ["orchestration"],
            AllowedTools = [.. Tools.AgentToolCatalog.ResolveToolsForCapabilities(["research"]), "test_charge_card"],
            GrantedPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages,
            Budget = new ResourceBudget { MaxTokens = 200_000, MaxToolCalls = 50 },
            TaskId = taskId,
            JournalPath = "r",
            Replay = replay,
            AutoStart = true
        });
        return agentId;
    }

    private async Task<AgentSnapshot> WaitForCompletionAsync(string rootId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var root = await _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId).GetSnapshot();
            if (root.Status == AgentStatus.Completed)
            {
                var team = await Registry.FindAsync(new FindAgentsQuery { RootAgentId = rootId });
                if (team.All(a => a.Status == AgentStatus.Completed)) return root;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The run didn't complete.");
    }

    private async Task<List<(string Path, string Role)>> TreeAsync(string rootId)
    {
        var team = await Registry.FindAsync(new FindAgentsQuery { RootAgentId = rootId });
        var snapshots = await Task.WhenAll(team.Select(a => _cluster.GrainFactory.GetGrain<IAgentGrain>(a.AgentId).GetSnapshot()));
        return snapshots.Select(s => (s.JournalPath, s.Role)).OrderBy(x => x.JournalPath).ToList();
    }

    private static string? Summary(IReadOnlyList<JournalStep> steps, string path) => steps
        .Where(s => s.AgentPath == path && s.Kind == JournalStep.ToolKind && s.ToolName == "complete_task")
        .Select(s => s.Key).FirstOrDefault() is { } key
        ? steps.Where(s => s.AgentPath == path && s.Kind == JournalStep.LlmKind)
            .Select(s => ReplayPolicy.LlmResponse(s.PayloadJson))
            .SelectMany(r => r.ToolCalls).First(c => c.Id == key).ArgumentsJson
        : null;

    [Fact]
    public async Task Full_replay_reproduces_the_tree_messages_and_result_without_the_model_or_side_effects()
    {
        ScriptedLlmProviderRegistry.Current = r => Original(r);
        var originalTask = "orig-" + Guid.NewGuid().ToString("n")[..8];
        var originalRoot = await StartRootAsync(originalTask, null);
        await WaitForCompletionAsync(originalRoot);
        Assert.Equal(1, TestSideEffects.Count("test_charge_card"));
        var original = await TestJournal.Instance.ListAsync(originalTask);

        // From here on any model call fails the test: everything must come from the journal.
        _liveCalls = 0;
        ScriptedLlmProviderRegistry.Current = _ => throw new InvalidOperationException("A replay called the live model.");
        var replayTask = "replay-" + Guid.NewGuid().ToString("n")[..8];
        var replayRoot = await StartRootAsync(replayTask, new ReplaySpec { SourceTaskId = originalTask, Mode = ReplayMode.Full });
        var replayedRoot = await WaitForCompletionAsync(replayRoot);
        var replay = await TestJournal.Instance.ListAsync(replayTask);

        // Same tree (by position and role), no new payment, same final summary.
        Assert.Equal(await TreeAsync(originalRoot), await TreeAsync(replayRoot));
        Assert.Equal(1, TestSideEffects.Count("test_charge_card"));
        Assert.Equal(Summary(original, "r"), Summary(replay, "r"));
        Assert.Contains("Summary: bought the data", Summary(replay, "r"));
        Assert.Equal(AgentStatus.Completed, replayedRoot.Status);

        // Every decision is identical, and so is every message sent.
        var diff = RunDiff.Compare(original, replay);
        Assert.All(diff.Steps.Where(s => s.Kind == JournalStep.LlmKind), s => Assert.Equal(StepDiffStatus.Same, s.Status));
        Assert.Empty(diff.AgentsOnlyInA);
        Assert.Empty(diff.AgentsOnlyInB);
        Assert.All(diff.Steps.Where(s => s.ToolName is "send_message" or "spawn_agent" or "complete_task" or "test_charge_card"),
            s => Assert.Equal(StepDiffStatus.Same, s.Status));
        Assert.True(diff.Identical, string.Join("\n", diff.Steps.Where(s => s.Status != StepDiffStatus.Same)
            .Select(s => $"{s.Status} {s.AgentPath} {s.Kind} {s.ToolName}: {s.SummaryA} | {s.SummaryB}")));
    }

    [Fact]
    public async Task Fork_replays_up_to_the_step_then_runs_live()
    {
        ScriptedLlmProviderRegistry.Current = r => Original(r);
        var originalTask = "orig-" + Guid.NewGuid().ToString("n")[..8];
        await WaitForCompletionAsync(await StartRootAsync(originalTask, null));
        var original = await TestJournal.Instance.ListAsync(originalTask);

        // Fork right after the root's first decision (spawning the team): the children and the
        // root's summary are live, with a model that now summarizes differently.
        var forkAfter = original.First(s => s.AgentPath == "r" && s.Kind == JournalStep.LlmKind).Seq;
        _liveCalls = 0;
        ScriptedLlmProviderRegistry.Current = r => Original(r, "Forked summary");
        var forkTask = "fork-" + Guid.NewGuid().ToString("n")[..8];
        var forkRoot = await StartRootAsync(forkTask, new ReplaySpec { SourceTaskId = originalTask, Mode = ReplayMode.Fork, ForkAfterSeq = forkAfter });
        await WaitForCompletionAsync(forkRoot);
        var fork = await TestJournal.Instance.ListAsync(forkTask);

        Assert.True(_liveCalls > 0, "steps after the fork point run live");
        var diff = RunDiff.Compare(original, fork);
        Assert.Equal(StepDiffStatus.Same, diff.Steps.First(s => s.AgentPath == "r" && s.Kind == JournalStep.LlmKind && s.Step == 1).Status);
        Assert.Contains("Forked summary", Summary(fork, "r"));
        Assert.DoesNotContain("Forked summary", Summary(original, "r"));
        // The team was spawned from the recorded decision, so it sits at the same positions.
        Assert.Empty(diff.AgentsOnlyInA);
    }

    [Fact]
    public async Task Fork_can_start_from_any_step()
    {
        ScriptedLlmProviderRegistry.Current = r => Original(r);
        var originalTask = "orig-" + Guid.NewGuid().ToString("n")[..8];
        await WaitForCompletionAsync(await StartRootAsync(originalTask, null));
        var original = await TestJournal.Instance.ListAsync(originalTask);

        foreach (var step in original.Where(s => s.Kind == JournalStep.LlmKind))
        {
            ScriptedLlmProviderRegistry.Current = r => Original(r, "Live");
            var forkTask = $"fork{step.Seq}-" + Guid.NewGuid().ToString("n")[..6];
            await WaitForCompletionAsync(await StartRootAsync(forkTask, new ReplaySpec { SourceTaskId = originalTask, Mode = ReplayMode.Fork, ForkAfterSeq = step.Seq }));
            var diff = RunDiff.Compare(original, await TestJournal.Instance.ListAsync(forkTask));
            // Everything recorded up to the fork point is replayed exactly.
            Assert.All(diff.Steps.Where(s => s.Kind == JournalStep.LlmKind && s.SeqA <= step.Seq), s => Assert.Equal(StepDiffStatus.Same, s.Status));
        }

    }
}
