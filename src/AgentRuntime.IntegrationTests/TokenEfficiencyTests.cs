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

/// <summary>Model routing and context compaction on real grains.</summary>
public sealed class TokenEfficiencyTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        _cluster = await DurableTestCluster.StartAsync(new Dictionary<string, string?>
        {
            ["Llm:Model"] = "main-model",
            ["Llm:FastModel"] = "fast-model",
            ["Llm:CompactAboveTokens"] = "300",
            ["Llm:CompactKeepRecentEntries"] = "4",
            ["Workspaces:StandingContextWindow"] = "8"
        });
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.DisposeAsync();
    }

    private static ToolCall Call(string name, object? args = null) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args ?? new { })
    };

    private static LlmCompletionResponse Respond(string? content, params ToolCall[] calls) =>
        new() { Content = content, ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls, InputTokens = 100 };

    private static bool IsSummary(LlmCompletionRequest r) => r.Messages[0].Content?.StartsWith(ContextCompactor.SystemMarker) == true;
    private static string Role(LlmCompletionRequest r) =>
        r.Messages[0].Content?.Contains("acting as: Coordinator.") == true ? "coordinator"
        : r.Messages[0].Content?.Contains("acting as: Stock Monitor.") == true ? "monitor" : "other";

    private LlmCompletionResponse SummaryReply(LlmCompletionRequest r) =>
        new() { Content = $"SUMMARY-{_requests.Count(IsSummary)}: checks so far were fine.", FinishReason = LlmFinishReason.Stop, InputTokens = 200, OutputTokens = 30 };

    [Fact]
    public async Task StandingAgents_UseTheFastModel_AndCompactHistoryIntoASummary()
    {
        ScriptedLlmProviderRegistry.Current = r =>
        {
            _requests.Enqueue(r);
            if (IsSummary(r)) return SummaryReply(r);

            var last = r.Messages[^1];
            var input = r.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
            if (Role(r) == "coordinator")
            {
                if (last.Role == ChatRole.Tool) return Respond(null, Call("wait_for_events", new { summary = "ok" }));
                return input.Contains("Your goal:")
                    ? Respond(null, Call("spawn_agent", new { role = "Stock Monitor", goal = "Watch stock", standing = true }))
                    : Respond(null, Call("wait_for_events", new { summary = "ok" }));
            }

            if (last.Role == ChatRole.Tool) return Respond(null, Call("wait_for_events", new { summary = "watching" }));
            if (input.Contains("Your goal:")) return Respond(null, Call("create_schedule", new { name = "check", instruction = "check", every_minutes = 1.0 / 60 }));
            return Respond(null, Call("notify_user", new { text = "all good" }));
        };

        var id = WorkspaceIds.New();
        var ws = _cluster.Client.GetGrain<IWorkspaceGrain>(id);
        await ws.Create(new WorkspaceCreationRequest { Name = "Shop", Goal = "Watch stock." });

        // Enough scheduled wake-ups for the monitor's history to pass its window twice.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (_requests.Count(IsSummary) < 2 && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.True(_requests.Count(IsSummary) >= 2, "history was never compacted");

        var nonSummary = _requests.Where(r => !IsSummary(r)).ToList();
        Assert.All(nonSummary.Where(r => Role(r) == "coordinator"), r => Assert.Equal("main-model", r.Model));
        Assert.All(nonSummary.Where(r => Role(r) == "monitor"), r => Assert.Equal("fast-model", r.Model));
        Assert.All(_requests.Where(IsSummary), r => Assert.Equal("fast-model", r.Model));

        // After compacting, the monitor carries the summary instead of its old entries.
        var later = nonSummary.Last(r => Role(r) == "monitor" && r.Messages[0].Content!.Contains("EARLIER CONTEXT"));
        Assert.Contains("SUMMARY-", later.Messages[0].Content);
        Assert.True(later.Messages.Count - 1 <= 8 + 3, $"monitor sent {later.Messages.Count - 1} history entries");
    }

    [Fact]
    public async Task TaskAgent_CompactsLongHistory_AndKeepsWorkingFromTheSummary()
    {
        var bulky = new string('x', 900); // each step adds ~225 tokens; the threshold is 300
        ScriptedLlmProviderRegistry.Current = r =>
        {
            _requests.Enqueue(r);
            if (IsSummary(r)) return SummaryReply(r);
            var steps = r.Messages.Count(m => m.Role == ChatRole.Tool);
            var summarized = r.Messages[0].Content!.Contains("SUMMARY-");
            return summarized && steps >= 1
                ? Respond(null, Call("complete_task", new { status = "completed", summary = "done after compaction" }))
                : Respond($"Step notes: {bulky}", Call("test_send_receipt"));
        };

        var agentId = $"root-{Guid.NewGuid():n}"[..14];
        var registry = _cluster.Client.GetGrain<IAgentRegistryGrain>(0);
        await registry.RegisterAsync(new AgentDirectoryEntry { AgentId = agentId, Role = "Root Agent", Goal = "Send receipts", RootAgentId = agentId });
        await _cluster.Client.GetGrain<IAgentGrain>(agentId).Initialize(new AgentInitializationRequest
        {
            AgentId = agentId,
            RootAgentId = agentId,
            Name = "Root",
            Role = "Root Agent",
            Goal = "Send receipts",
            AllowedTools = ["test_send_receipt", "complete_task"],
            Budget = new ResourceBudget { MaxTokens = 1_000_000, MaxToolCalls = 100, MaxDurationSeconds = 600 },
            TaskId = $"task-{Guid.NewGuid():n}",
            AutoStart = true
        });

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await registry.GetAsync(agentId) is not { Status: AgentStatus.Completed } && DateTime.UtcNow < deadline) await Task.Delay(200);

        Assert.Equal(AgentStatus.Completed, (await registry.GetAsync(agentId))!.Status);
        var compactedCall = _requests.First(r => !IsSummary(r) && r.Messages[0].Content!.Contains("SUMMARY-"));
        // The kickoff message was folded into the summary; only recent steps are resent.
        Assert.DoesNotContain(compactedCall.Messages, m => m.Content?.StartsWith("Your goal:") == true);
        Assert.True(compactedCall.Messages.Count - 1 <= 4 + 1);
    }
}
