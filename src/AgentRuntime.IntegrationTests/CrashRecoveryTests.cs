using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Messaging;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Durable execution (docs/durability.md): the silo is killed abruptly while an agent is in the
/// middle of a tool call, a fresh silo starts with only the durable stores, and the agent must
/// resume on its own — without repeating side effects that aren't safe to repeat, without losing
/// or duplicating mail, and without anyone poking it.
/// </summary>
public sealed class CrashRecoveryTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;

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

    private IGrainFactory Grains => _cluster.Client;
    private IAgentRegistryGrain Registry => Grains.GetGrain<IAgentRegistryGrain>(0);

    private static ToolCall Call(string name, object? args = null) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args ?? new { })
    };

    private static LlmCompletionResponse Respond(params ToolCall[] calls) => new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    private static string? LastToolResult(LlmCompletionRequest r) =>
        r.Messages.LastOrDefault(m => m.Role == ChatRole.Tool)?.Content;

    private async Task<string> StartAgentAsync(string goal, params string[] tools)
    {
        var agentId = $"root-{Guid.NewGuid():n}"[..14];
        await Registry.RegisterAsync(new AgentDirectoryEntry
        {
            AgentId = agentId,
            Role = "Root Agent",
            Goal = goal,
            Status = AgentStatus.Created,
            RootAgentId = agentId
        });
        await Grains.GetGrain<IAgentGrain>(agentId).Initialize(new AgentInitializationRequest
        {
            AgentId = agentId,
            RootAgentId = agentId,
            Name = "Root",
            Role = "Root Agent",
            Goal = goal,
            AllowedTools = [.. tools, "complete_task"],
            GrantedPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages,
            Budget = new ResourceBudget { MaxTokens = 100_000, MaxToolCalls = 50, MaxDurationSeconds = 600 },
            TaskId = $"task-{Guid.NewGuid():n}",
            AutoStart = true
        });
        return agentId;
    }

    /// <summary>Waits on the registry (not the agent itself), so the test never re-activates the
    /// agent: any recovery has to come from the runtime's own reminders.</summary>
    private async Task<AgentDirectoryEntry> WaitForRegistryStatusAsync(string agentId, AgentStatus status, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        AgentDirectoryEntry? entry = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                entry = await Registry.GetAsync(agentId);
                if (entry?.Status == status) return entry;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                // The cluster is still recovering from the kill; try again.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Agent {agentId} did not reach {status} (last seen: {entry?.Status}).");
    }

    [Fact]
    public async Task NonIdempotentCall_InterruptedByCrash_IsNotRepeated_AndAgentIsToldOutcomeUnknown()
    {
        string? completionSummary = null;
        ScriptedLlmProviderRegistry.Current = r =>
        {
            var last = LastToolResult(r);
            if (last is null) return Respond(Call("test_charge_card"));
            completionSummary = last;
            return Respond(Call("complete_task", new { status = "completed", summary = last }));
        };

        var entered = TestSideEffects.BlockNextCall("test_charge_card");
        var agentId = await StartAgentAsync("Charge the card once.", "test_charge_card");
        await entered.WaitAsync(TimeSpan.FromSeconds(30));

        await _cluster.CrashAndRestartAsync();
        await WaitForRegistryStatusAsync(agentId, AgentStatus.Completed);

        Assert.Equal(1, TestSideEffects.Count("test_charge_card"));
        Assert.NotNull(completionSummary);
        Assert.Contains("Outcome unknown", completionSummary);
    }

    [Fact]
    public async Task IdempotentCall_InterruptedByCrash_IsRetriedWithTheSameIdempotencyKey()
    {
        ScriptedLlmProviderRegistry.Current = r => LastToolResult(r) is null
            ? Respond(Call("test_send_receipt"))
            : Respond(Call("complete_task", new { status = "completed", summary = "Receipt sent." }));

        var entered = TestSideEffects.BlockNextCall("test_send_receipt");
        var agentId = await StartAgentAsync("Send the receipt.", "test_send_receipt");
        await entered.WaitAsync(TimeSpan.FromSeconds(30));

        await _cluster.CrashAndRestartAsync();
        await WaitForRegistryStatusAsync(agentId, AgentStatus.Completed);

        var keys = TestSideEffects.Executions.Where(e => e.Tool == "test_send_receipt").Select(e => e.Key).ToList();
        Assert.Equal(2, keys.Count);
        Assert.Single(keys.Distinct());
        Assert.StartsWith(agentId + ":", keys[0]);
    }

    [Fact]
    public async Task MessageSentBeforeCrash_IsDeliveredExactlyOnce_AfterRecovery()
    {
        var seenCounts = new List<int>();
        ScriptedLlmProviderRegistry.Current = r =>
        {
            var copies = r.Messages.Count(m => m.Role == ChatRole.User && m.Content?.Contains("the password is swordfish") == true);
            if (LastToolResult(r) is null) return Respond(Call("test_send_receipt"));
            seenCounts.Add(copies);
            return copies > 0
                ? Respond(Call("complete_task", new { status = "completed", summary = $"Saw the message {copies} time(s)." }))
                : new LlmCompletionResponse { Content = "Waiting for the message.", FinishReason = LlmFinishReason.Stop };
        };

        var entered = TestSideEffects.BlockNextCall("test_send_receipt");
        var agentId = await StartAgentAsync("Wait for the password.", "test_send_receipt");
        await entered.WaitAsync(TimeSpan.FromSeconds(30));

        // Arrives while the agent is mid-call; it must be stored durably, not in memory.
        await Grains.GetGrain<IAgentGrain>(agentId).SendMessage(new AgentMessage
        {
            FromAgentId = "operator",
            ToAgentId = agentId,
            MessageType = MessageType.InformationResponse,
            Payload = "the password is swordfish"
        });

        await _cluster.CrashAndRestartAsync();
        await WaitForRegistryStatusAsync(agentId, AgentStatus.Completed);

        Assert.Equal(1, seenCounts.Last());
    }

    [Fact]
    public async Task StopRequestedBeforeCrash_IsHonouredAfterRecovery()
    {
        ScriptedLlmProviderRegistry.Current = r => LastToolResult(r) is null
            ? Respond(Call("test_send_receipt"))
            : Respond(Call("complete_task", new { status = "completed", summary = "Should have been stopped first." }));

        var entered = TestSideEffects.BlockNextCall("test_send_receipt");
        var agentId = await StartAgentAsync("Do something slow.", "test_send_receipt");
        await entered.WaitAsync(TimeSpan.FromSeconds(30));

        await Grains.GetGrain<IAgentGrain>(agentId).Stop();
        await _cluster.CrashAndRestartAsync();

        // The interrupted call finishes, then the durable stop applies at the next safe point.
        await WaitForRegistryStatusAsync(agentId, AgentStatus.Terminated);
    }
}
