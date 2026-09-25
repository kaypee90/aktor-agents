using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Workspaces end to end with a scripted LLM: the coordinator turning the user's goal into a
/// standing agent with a schedule, commands while running, webhooks (auth, dedupe, rate limit,
/// secrecy), the daily budget, pause, and schedules surviving a crash.
/// </summary>
public sealed class WorkspaceTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        _cluster = await DurableTestCluster.StartAsync(new Dictionary<string, string?>
        {
            ["Workspaces:MaxWebhookEventsPerMinute"] = "3"
        });
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

    private static LlmCompletionResponse Respond(int tokens, params ToolCall[] calls) =>
        new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls, InputTokens = tokens };

    private static bool IsCoordinator(LlmCompletionRequest r) => r.Messages[0].Content?.Contains("acting as: Coordinator.") == true;

    private static string LastInput(LlmCompletionRequest r) =>
        r.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;

    private static bool AfterToolResult(LlmCompletionRequest r) => r.Messages[^1].Role == ChatRole.Tool;

    /// <summary>A realistic setup script: the coordinator spawns a standing monitor for the goal,
    /// the monitor gives itself a schedule and reports each time it fires.</summary>
    private Func<LlmCompletionRequest, LlmCompletionResponse> MonitorScript(int tokensPerCall = 0, double everyMinutes = 1.0 / 60)
    {
        return r =>
        {
            _requests.Enqueue(r);
            var input = LastInput(r);

            if (IsCoordinator(r))
            {
                if (AfterToolResult(r)) return Respond(tokensPerCall, Call("wait_for_events", new { summary = "Set up." }));
                if (input.Contains("Your goal:"))
                {
                    return Respond(tokensPerCall,
                        Call("spawn_agent", new { role = "Stock Monitor", goal = "Watch stock levels", standing = true }),
                        Call("notify_user", new { text = "I've set up a Stock Monitor." }));
                }

                if (input.Contains("[Message from the user"))
                {
                    return Respond(tokensPerCall, Call("notify_user", new { text = "ack: " + input.Split('\n').Last() }));
                }

                return Respond(tokensPerCall, Call("wait_for_events", new { summary = "Idle." }));
            }

            if (AfterToolResult(r)) return Respond(tokensPerCall, Call("wait_for_events", new { summary = "Watching." }));
            if (input.Contains("Your goal:"))
            {
                return Respond(tokensPerCall, Call("create_schedule", new { name = "Stock check", instruction = "Check stock now.", every_minutes = everyMinutes }));
            }

            if (input.Contains("[Scheduled trigger")) return Respond(tokensPerCall, Call("notify_user", new { text = "Stock check done." }));
            if (input.Contains("[Webhook")) return Respond(tokensPerCall, Call("notify_user", new { text = "Webhook seen: " + input.Split('\n').Last() }));
            return Respond(tokensPerCall, Call("wait_for_events", new { summary = "Nothing to do." }));
        };
    }

    private async Task<string> CreateWorkspaceAsync(int? dailyTokens = null)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest
        {
            Name = "Shop",
            Goal = "Keep an eye on my stock levels and alert me when anything runs low.",
            DailyTokenLimit = dailyTokens
        });
        return id;
    }

    private async Task<WorkspaceSnapshot> WaitForAsync(string id, Func<WorkspaceSnapshot, bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        WorkspaceSnapshot? snapshot = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                snapshot = await Workspace(id).GetSnapshot();
                if (snapshot is not null && condition(snapshot)) return snapshot;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                // Cluster recovering.
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("Workspace condition not met. Conversation:\n" +
                                   string.Join("\n", snapshot?.Conversation.Select(c => $"[{c.AuthorName}] {c.Text}") ?? []));
    }

    private static int Count(WorkspaceSnapshot s, string text) => s.Conversation.Count(c => c.Text.Contains(text));

    [Fact]
    public async Task Goal_BecomesAStandingAgentWithASchedule_ThatReportsEachTimeItFires()
    {
        ScriptedLlmProviderRegistry.Current = MonitorScript();
        var id = await CreateWorkspaceAsync();

        var s = await WaitForAsync(id, s => Count(s, "Stock check done.") >= 2);

        Assert.Contains(s.Conversation, c => c.Text == "I've set up a Stock Monitor.");
        var monitor = Assert.Single(s.Agents, a => a.Role == "Stock Monitor");
        Assert.True(monitor.Standing);
        var trigger = Assert.Single(s.Triggers);
        Assert.Equal(monitor.AgentId, trigger.TargetAgentId);
        Assert.Equal(1, trigger.IntervalSeconds);
        // Standing agents wait between events instead of finishing.
        Assert.All(s.Agents, a => Assert.Equal("Waiting", a.Status));
    }

    [Fact]
    public async Task UserCommand_WhileRunning_ReachesTheCoordinatorOnce()
    {
        ScriptedLlmProviderRegistry.Current = MonitorScript(everyMinutes: 60);
        var id = await CreateWorkspaceAsync();
        await WaitForAsync(id, s => s.Triggers.Count == 1);

        // A client retry with the same client message id must not deliver the command twice.
        await Workspace(id).PostUserMessage("also watch prices", null, "client-1");
        await Workspace(id).PostUserMessage("also watch prices", null, "client-1");

        var s = await WaitForAsync(id, s => Count(s, "ack: also watch prices") >= 1);
        await Task.Delay(1500);
        s = (await Workspace(id).GetSnapshot())!;
        Assert.Equal(1, Count(s, "ack: also watch prices"));
        Assert.Equal(1, s.Conversation.Count(c => c.AuthorKind == ChatAuthorKind.User && c.Text == "also watch prices"));
    }

    [Fact]
    public async Task Webhook_IsAuthenticated_Deduplicated_RateLimited_AndItsSecretNeverReachesAgents()
    {
        ScriptedLlmProviderRegistry.Current = MonitorScript(everyMinutes: 60);
        var id = await CreateWorkspaceAsync();
        var ready = await WaitForAsync(id, s => s.Agents.Any(a => a.Role == "Stock Monitor" && a.Status == "Waiting"));
        var monitor = ready.Agents.Single(a => a.Role == "Stock Monitor").AgentId;

        var created = await Workspace(id).AddTrigger(new TriggerSpec
        {
            Kind = TriggerKind.Webhook,
            Name = "Shopify inventory",
            Instruction = "Check the item in the payload.",
            TargetAgentId = monitor
        }, "user", string.Empty, revealSecret: true);
        Assert.True(created.Success, created.Message);
        var view = JsonSerializer.Deserialize<JsonElement>(created.ResultJson!);
        var triggerId = view.GetProperty("trigger_id").GetString()!;
        var secret = view.GetProperty("webhook_path").GetString()!.Split('/').Last();

        WebhookDelivery Delivery(string body, string? deliveryId, string? token = null) =>
            new() { TriggerId = triggerId, Token = token ?? secret, Body = body, DeliveryId = deliveryId };

        Assert.Equal(WebhookOutcome.NotFound, await Workspace(id).DeliverWebhook(Delivery("{}", "d0", token: "wrong")) switch
        {
            WebhookOutcome.Unauthorized => WebhookOutcome.NotFound, // the API maps both to 404
            var other => other
        });
        Assert.Equal(WebhookOutcome.Accepted, await Workspace(id).DeliverWebhook(Delivery("""{"sku":"A1","qty":3}""", "d1")));
        Assert.Equal(WebhookOutcome.Duplicate, await Workspace(id).DeliverWebhook(Delivery("""{"sku":"A1","qty":3}""", "d1")));
        Assert.Equal(WebhookOutcome.Accepted, await Workspace(id).DeliverWebhook(Delivery("""{"sku":"B2","qty":1}""", "d2")));
        Assert.Equal(WebhookOutcome.Accepted, await Workspace(id).DeliverWebhook(Delivery("""{"sku":"C3","qty":0}""", "d3")));
        Assert.Equal(WebhookOutcome.RateLimited, await Workspace(id).DeliverWebhook(Delivery("""{"sku":"D4","qty":0}""", "d4")));

        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.Contains("C3")));
        await Task.Delay(1000);
        s = (await Workspace(id).GetSnapshot())!;

        Assert.Equal(1, Count(s, "A1"));   // delivered once despite the redelivery
        Assert.Equal(0, Count(s, "D4"));   // rate limited
        Assert.Equal(1, s.Triggers.Single(t => t.TriggerId == triggerId).DroppedCount);
        // The user saw the secret URL in chat; no agent ever did.
        Assert.Contains(s.Conversation, c => c.AuthorKind == ChatAuthorKind.System && c.Text.Contains(secret));
        Assert.DoesNotContain(_requests, r => r.Messages.Any(m => m.Content?.Contains(secret) == true));
        Assert.All(s.Triggers, t => Assert.Null(t.WebhookPath));
    }

    [Fact]
    public async Task DailyBudget_StopsLlmCallsOnceUsedUp_AndTellsTheUserOnce()
    {
        // Each LLM call reports 3,000 tokens against a 10,000-token day.
        ScriptedLlmProviderRegistry.Current = MonitorScript(tokensPerCall: 3_000);
        var id = await CreateWorkspaceAsync(dailyTokens: 10_000);

        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.Contains("Agents are paused for today")));
        var callsWhenStopped = _requests.Count;
        await Task.Delay(4000); // several schedule ticks

        s = (await Workspace(id).GetSnapshot())!;
        Assert.InRange(_requests.Count - callsWhenStopped, 0, 1); // at most one call already in flight
        Assert.Equal(1, s.Conversation.Count(c => c.Text.Contains("Agents are paused for today")));
        Assert.True(s.TokensToday >= 10_000);
    }

    [Fact]
    public async Task Pause_StopsTriggers_AndResume_StartsThemAgain()
    {
        ScriptedLlmProviderRegistry.Current = MonitorScript();
        var id = await CreateWorkspaceAsync();
        await WaitForAsync(id, s => Count(s, "Stock check done.") >= 1);

        await Workspace(id).Pause();
        var paused = (await Workspace(id).GetSnapshot())!;
        await Task.Delay(3500);
        var stillPaused = (await Workspace(id).GetSnapshot())!;
        Assert.Equal(WorkspaceStatus.Paused, stillPaused.Status);
        Assert.InRange(Count(stillPaused, "Stock check done.") - Count(paused, "Stock check done."), 0, 1);

        await Workspace(id).Resume();
        var before = Count((await Workspace(id).GetSnapshot())!, "Stock check done.");
        await WaitForAsync(id, s => Count(s, "Stock check done.") >= before + 2);
    }

    [Fact]
    public async Task Schedule_KeepsFiringAfterACrash()
    {
        ScriptedLlmProviderRegistry.Current = MonitorScript();
        var id = await CreateWorkspaceAsync();
        var before = await WaitForAsync(id, s => Count(s, "Stock check done.") >= 1);

        await _cluster.CrashAndRestartAsync();
        await Task.Delay(TimeSpan.FromSeconds(5)); // the reminders, not our polling, must revive it

        var after = await WaitForAsync(id, s => Count(s, "Stock check done.") >= Count(before, "Stock check done.") + 2, 60);
        Assert.Single(after.Triggers);
        Assert.Single(after.Agents, a => a.Role == "Stock Monitor");
    }
}
