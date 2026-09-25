using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Integrations;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>Compiled watches: recurring checks the runtime evaluates without the LLM, reporting
/// only newly matching items.</summary>
public sealed class WatchTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();
    private string _mode = "notify";
    private string _sourceTool = "crm__list_inventory";
    private readonly ConcurrentQueue<string> _toolResults = new();

    public async Task InitializeAsync()
    {
        FakeCrmPlugin.Reset();
        InMemorySecretStore.Values.Clear();
        _cluster = await DurableTestCluster.StartAsync(new Dictionary<string, string?> { ["Integrations:NotificationRetryBaseSeconds"] = "0" });
        ScriptedLlmProviderRegistry.Current = Script;
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.DisposeAsync();
    }

    private IWorkspaceGrain Workspace(string id) => _cluster.Client.GetGrain<IWorkspaceGrain>(id);

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static LlmCompletionResponse Respond(params ToolCall[] calls) => new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        _requests.Enqueue(r);
        var last = r.Messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            _toolResults.Enqueue(last.Content ?? string.Empty);
            return Respond(Call("wait_for_events", new { summary = "Watching." }));
        }

        var input = last.Content ?? string.Empty;
        if (input.Contains("[Message from the user") && input.Contains("watch stock"))
        {
            return Respond(Call("create_watch", new
            {
                name = "Low stock",
                source_tool = _sourceTool,
                items_path = "$.body.items[*]",
                conditions = new[] { new { field = "qty", op = "<", value = "10" } },
                key_field = "sku",
                display_fields = new[] { "sku", "qty" },
                every_minutes = 1.0 / 60,
                mode = _mode,
                urgency = "urgent",
                message = "Low stock: {items}",
                instruction = "Decide how much to reorder."
            }));
        }

        if (input.Contains("[Watch 'Low stock'")) return Respond(Call("notify_user", new { text = "Agent saw: " + input.Split('\n').Last() }));
        return Respond(Call("wait_for_events", new { summary = "Idle." }));
    }

    private async Task<string> WorkspaceWithCrmAsync()
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = "Shop", Goal = "Run my shop." });
        await WaitForAsync(id, s => s.Agents.Any(a => a.Status == "Waiting"));
        var c = await Workspace(id).AddConnection(new ConnectionRequest
        {
            PluginId = "fake-crm",
            Name = "crm",
            Settings = new() { ["region"] = "eu" },
            Secrets = new() { ["api_key"] = "k" },
            NotifyLevel = NotifyLevel.Urgent
        });
        Assert.True(c.Success, c.Message);
        return id;
    }

    private async Task<WorkspaceSnapshot> WaitForAsync(string id, Func<WorkspaceSnapshot, bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        WorkspaceSnapshot? s = null;
        while (DateTime.UtcNow < deadline)
        {
            s = await Workspace(id).GetSnapshot();
            if (s is not null && condition(s)) return s;
            await Task.Delay(150);
        }

        throw new TimeoutException("Condition not met. Conversation:\n" + string.Join("\n", s?.Conversation.Select(c => $"[{c.AuthorName}] {c.Text}") ?? []));
    }

    private static int Count(WorkspaceSnapshot s, string text) => s.Conversation.Count(c => c.Text.Contains(text));

    [Fact]
    public async Task Watch_AlertsOncePerNewlyMatchingItem_WithoutCallingTheLlm()
    {
        FakeCrmPlugin.Inventory["MUG"] = 3;
        FakeCrmPlugin.Inventory["TEE"] = 40;
        var id = await WorkspaceWithCrmAsync();

        await Workspace(id).PostUserMessage("watch stock", null, null);
        var s = await WaitForAsync(id, s => Count(s, "Low stock: sku=MUG, qty=3") == 1);

        // The agent's dry run showed what the rule sees right now.
        var dryRun = JsonDocument.Parse(_toolResults.First()).RootElement.GetProperty("dry_run");
        Assert.Equal(2, dryRun.GetProperty("items_found").GetInt32());
        Assert.Equal(1, dryRun.GetProperty("matching_now").GetInt32());

        var llmCallsAfterSetup = _requests.Count;
        await WaitForAsync(id, s => s.Triggers.Single().Checks >= 4);

        // Still low, so not reported again; then TEE drops and only TEE is reported.
        FakeCrmPlugin.Inventory["TEE"] = 2;
        s = await WaitForAsync(id, s => Count(s, "Low stock: sku=TEE, qty=2") == 1);
        var checksAtTeeAlert = s.Triggers.Single().Checks;
        s = await WaitForAsync(id, s => s.Triggers.Single().Checks >= checksAtTeeAlert + 2 && s.PendingNotifications == 0);

        Assert.Equal(1, Count(s, "sku=TEE")); // two more checks, no repeat

        Assert.Equal(1, Count(s, "sku=MUG"));
        Assert.Equal(llmCallsAfterSetup, _requests.Count); // every check ran without the LLM
        var watch = s.Triggers.Single();
        Assert.Equal(TriggerKind.Watch, watch.Kind);
        Assert.Equal(2, watch.Alerts);
        Assert.Equal(watch.Checks, s.LlmCallsAvoided); // notify mode: every check avoided an LLM call

        // Urgent alerts reached the user's channel.
        await WaitForAsync(id, _ => FakeCrmPlugin.Notifications.Count(n => n.Text.StartsWith("Low stock")) == 2);
    }

    [Fact]
    public async Task WakeAgentMode_WakesTheAgentWithOnlyTheMatchingItems()
    {
        _mode = "wake_agent";
        FakeCrmPlugin.Inventory["MUG"] = 3;
        FakeCrmPlugin.Inventory["TEE"] = 40;
        var id = await WorkspaceWithCrmAsync();

        await Workspace(id).PostUserMessage("watch stock", null, null);
        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.StartsWith("Agent saw:")));

        var woke = _requests.Select(r => r.Messages[^1].Content ?? string.Empty).Single(c => c.Contains("[Watch 'Low stock'"));
        Assert.Contains("sku=MUG, qty=3", woke);
        Assert.DoesNotContain("TEE", woke);
        Assert.Contains("Decide how much to reorder.", woke);
    }

    [Fact]
    public async Task Watch_RefusesToolsThatCanChangeData()
    {
        _sourceTool = "crm__delete_customer";
        var id = await WorkspaceWithCrmAsync();

        await Workspace(id).PostUserMessage("watch stock", null, null);
        await WaitForAsync(id, _ => !_toolResults.IsEmpty);

        Assert.Contains("read-only", JsonDocument.Parse(_toolResults.First()).RootElement.GetProperty("error").GetString());
        Assert.Empty((await Workspace(id).GetSnapshot())!.Triggers);
        Assert.DoesNotContain(FakeCrmPlugin.ToolCalls, c => c.Tool == "delete_customer");
    }
}
