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
/// only newly matching items, or starting a run of the pipeline with just those items.</summary>
public sealed class WatchTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();
    private string _mode = "notify";
    private string _sourceTool = "crm__list_inventory";

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

    /// <summary>The pipeline's one stage reports the run's input: in run mode, the watch's matches.</summary>
    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        _requests.Enqueue(r);
        var input = r.Messages[^1].Content ?? string.Empty;
        var matches = input.Split('\n').FirstOrDefault(l => l.Contains("sku=")) ?? "nothing";
        return Respond(Call("complete_task", new { status = "completed", summary = "Agent saw: " + matches }));
    }

    private async Task<string> WorkspaceWithCrmAsync()
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest
        {
            Name = "Shop",
            Goal = "Run my shop.",
            Pipeline = new Pipelines.PipelineDefinition
            {
                Stages = [new Pipelines.PipelineStage { StageId = "reorder", Name = "Reorder", Instructions = "Decide what to reorder.", Retries = 0 }]
            }
        });
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

    /// <summary>The watch a person sets up on the Triggers tab: low stock, checked every second.</summary>
    private Task<WorkspaceActionResult> AddWatchAsync(string id) => Workspace(id).AddTrigger(new TriggerSpec
    {
        Kind = TriggerKind.Watch,
        Name = "Low stock",
        SourceTool = _sourceTool,
        Rule = new WatchRule
        {
            ItemsPath = "$.body.items[*]",
            Conditions = [new WatchCondition { Field = "qty", Op = "<", Value = "10" }],
            KeyField = "sku",
            DisplayFields = ["sku", "qty"]
        },
        EveryMinutes = 1.0 / 60,
        WatchMode = _mode,
        Urgency = "urgent",
        MessageTemplate = "Low stock: {items}",
        Instruction = "Decide how much to reorder."
    }, "user", string.Empty, revealSecret: false);

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

        var created = await AddWatchAsync(id);
        Assert.True(created.Success, created.Message);
        var s = await WaitForAsync(id, s => Count(s, "Low stock: sku=MUG, qty=3") == 1);

        // The dry run at creation showed what the rule sees right now.
        var dryRun = JsonDocument.Parse(created.ResultJson!).RootElement.GetProperty("dry_run");
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
        Assert.Empty(s.Runs);
        var watch = s.Triggers.Single();
        Assert.Equal(TriggerKind.Watch, watch.Kind);
        Assert.Equal(2, watch.Alerts);
        Assert.Equal(watch.Checks, s.LlmCallsAvoided); // notify mode: every check avoided an LLM call

        // Urgent alerts reached the user's channel.
        await WaitForAsync(id, _ => FakeCrmPlugin.Notifications.Count(n => n.Text.StartsWith("Low stock")) == 2);
    }

    [Fact]
    public async Task RunMode_StartsARunWithOnlyTheMatchingItems()
    {
        _mode = "run";
        FakeCrmPlugin.Inventory["MUG"] = 3;
        FakeCrmPlugin.Inventory["TEE"] = 40;
        var id = await WorkspaceWithCrmAsync();

        Assert.True((await AddWatchAsync(id)).Success);
        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.Contains("Agent saw:")));

        var run = Assert.Single(s.Runs);
        Assert.Equal("watch", run.Source);
        Assert.Equal("Low stock", run.TriggerName);
        var input = _requests.Select(r => r.Messages[^1].Content ?? string.Empty).Single(c => c.Contains("[Watch 'Low stock'"));
        Assert.Contains("sku=MUG, qty=3", input);
        Assert.DoesNotContain("TEE", input);
        Assert.Contains("Decide how much to reorder.", input);
    }

    [Fact]
    public async Task Watch_RefusesToolsThatCanChangeData()
    {
        _sourceTool = "crm__delete_customer";
        var id = await WorkspaceWithCrmAsync();

        var created = await AddWatchAsync(id);

        Assert.False(created.Success);
        Assert.Contains("read-only", created.Message);
        Assert.Empty((await Workspace(id).GetSnapshot())!.Triggers);
        Assert.DoesNotContain(FakeCrmPlugin.ToolCalls, c => c.Tool == "delete_customer");
    }
}
