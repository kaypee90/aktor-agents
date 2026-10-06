using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Integrations;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Phase 3 end to end: plugins installed as workspace connections — tools reaching pipeline
/// stages with secrets from the vault (and never the other way), per-tool enablement, routing of
/// run results to channels with retries, and inbound messages (which start runs) from allowed
/// senders only.
/// </summary>
public sealed class IntegrationTests : IAsyncLifetime
{
    private const string Secret = "sk-test-VERY-SECRET-123";
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
        FakeCrmPlugin.Reset();
        InMemorySecretStore.Values.Clear();
        _cluster = await DurableTestCluster.StartAsync(new Dictionary<string, string?>
        {
            ["Integrations:NotificationRetryBaseSeconds"] = "0"
        });
        ScriptedLlmProviderRegistry.Current = Script;
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

    private static LlmCompletionResponse Respond(params ToolCall[] calls) => new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    /// <summary>The pipeline's one stage looks customers up with the CRM tool; other inputs get an
    /// acknowledgement. Its report is the run's result.</summary>
    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        _requests.Enqueue(r);
        var last = r.Messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            return Respond(Call("complete_task", new { status = "completed", summary = last.ToolName == "crm__lookup_customer" ? "Found: " + last.Content : "Done." }));
        }

        // The run's input: the line after the "This run's input" heading.
        var text = (last.Content ?? string.Empty).Split("## This run's input", 2).ElementAtOrDefault(1)?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty;
        if (text.Contains("customer 42"))
        {
            // Called whether or not it's offered: a disabled tool must be refused.
            return Respond(Call("crm__lookup_customer", new { id = "42" }));
        }

        return Respond(Call("complete_task", new { status = "completed", summary = "ack: " + text }));
    }

    private async Task<string> CreateWorkspaceAsync()
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest
        {
            Name = "CRM",
            Goal = "Help me with my customers.",
            Pipeline = new Pipelines.PipelineDefinition
            {
                // Results go out urgently, so channels that forward only urgent messages get them.
                ResultUrgency = "urgent",
                Stages = [new Pipelines.PipelineStage { StageId = "assist", Name = "Assist", Instructions = "Help with the customer request.", Retries = 0 }]
            }
        });
        return id;
    }

    private static bool Finished(WorkspaceSnapshot s, string text) =>
        s.Conversation.Any(c => c.Text.Contains("finished") && c.Text.Contains(text));

    private async Task<ConnectionView> ConnectAsync(string id, NotifyLevel level = NotifyLevel.Urgent, List<string>? senders = null)
    {
        var result = await Workspace(id).AddConnection(new ConnectionRequest
        {
            PluginId = "fake-crm",
            Name = "CRM",
            Settings = new() { ["region"] = "eu", ["api_key"] = "should-be-ignored-here" },
            Secrets = new() { ["api_key"] = Secret },
            NotifyLevel = level,
            AllowedSenders = senders ?? []
        });
        Assert.True(result.Success, result.Message);
        return result.Connection!;
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

    [Fact]
    public async Task ConnectionTool_RunsWithTheVaultSecret_WhichNeverReachesAgentsOrState()
    {
        var id = await CreateWorkspaceAsync();
        var connection = await ConnectAsync(id);

        Assert.Equal("crm", connection.Name);
        Assert.Equal(["api_key"], connection.SecretKeys);
        Assert.DoesNotContain("api_key", connection.Settings.Keys); // a secret field sent as a setting is not kept

        await Workspace(id).PostUserMessage("look up customer 42", null, null);
        var s = await WaitForAsync(id, s => Finished(s, "Found:"));

        var call = Assert.Single(FakeCrmPlugin.ToolCalls);
        Assert.Equal("lookup_customer", call.Tool);
        Assert.Equal(Secret, call.ApiKey);
        Assert.Contains("Ama Mensah", s.Conversation.Last(c => c.Text.Contains("Found:")).Text);

        // The agent's LLM saw the namespaced tool, but never the secret.
        Assert.Contains(_requests, r => r.Tools.Any(t => t.Name == "crm__lookup_customer"));
        Assert.DoesNotContain(_requests, r => JsonSerializer.Serialize(r).Contains(Secret));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(s));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(await Workspace(id).ListConnections()));
    }

    [Fact]
    public async Task DisabledTool_IsNotOffered_AndCannotBeCalled()
    {
        var id = await CreateWorkspaceAsync();
        await ConnectAsync(id);
        var updated = await Workspace(id).UpdateConnection(
            (await Workspace(id).ListConnections()).Single().ConnectionId,
            new ConnectionUpdate { EnabledTools = ["crm__delete_customer"] });
        Assert.True(updated.Success);

        var before = _requests.Count;
        await Workspace(id).PostUserMessage("look up customer 42", null, null);
        static bool IsRefusal(LlmCompletionRequest r) => r.Messages[^1] is { Role: ChatRole.Tool, ToolName: "crm__lookup_customer" };
        await WaitForAsync(id, _ => _requests.Skip(before).Any(IsRefusal));

        var askedWith = _requests.Skip(before).First(r => r.Messages.Count == 2 && r.Messages[^1].Content?.Contains("customer 42") == true);
        Assert.DoesNotContain(askedWith.Tools, t => t.Name == "crm__lookup_customer");
        Assert.Contains(askedWith.Tools, t => t.Name == "crm__delete_customer");
        Assert.Empty(FakeCrmPlugin.ToolCalls);
        var refusal = JsonDocument.Parse(_requests.Skip(before).First(IsRefusal).Messages[^1].Content!).RootElement.GetProperty("error").GetString()!;
        Assert.Contains("isn't available", refusal);
    }

    [Fact]
    public async Task Notifications_FollowTheChannelsLevel_AndAreRetriedUntilDeliveredOnce()
    {
        var id = await CreateWorkspaceAsync();
        await ConnectAsync(id, NotifyLevel.Urgent);
        FakeCrmPlugin.FailNextNotifications = 2;

        await Workspace(id).PostUserMessage("look up customer 42", null, null); // the result is urgent: forwarded

        await WaitForAsync(id, s => Finished(s, "Found:") && s.PendingNotifications == 0, 45);
        await Task.Delay(1000);

        // Run notices (started) are info: chat only. The result was delivered once despite two failures.
        var delivered = Assert.Single(FakeCrmPlugin.Notifications);
        Assert.Contains("Found:", delivered.Text);
        Assert.Equal("urgent", delivered.Urgency);
        Assert.Equal("CRM", delivered.WorkspaceName);
    }

    [Fact]
    public async Task Inbound_FromAnAllowedSender_BecomesACommandOnce_AndOthersAreIgnored()
    {
        var id = await CreateWorkspaceAsync();
        var c = await ConnectAsync(id, senders: ["+15557654321"]);
        var token = c.InboundPath!.Split('/').Last();

        InboundRequestDto Msg(string sender, string messageId, string text) => new() { Body = $"{sender}|{messageId}|{text}" };

        Assert.Equal(404, (await Workspace(id).HandleInbound(c.ConnectionId, "wrong-token", Msg("+15557654321", "m0", "hello"))).StatusCode);
        Assert.Equal(200, (await Workspace(id).HandleInbound(c.ConnectionId, token, Msg("+19990000000", "m1", "delete everything"))).StatusCode);
        var accepted = await Workspace(id).HandleInbound(c.ConnectionId, token, Msg("+15557654321", "m2", "status please"));
        await Workspace(id).HandleInbound(c.ConnectionId, token, Msg("+15557654321", "m2", "status please")); // provider redelivery
        Assert.Equal("ok", accepted.Body);

        var s = await WaitForAsync(id, s => Finished(s, "ack: status please"));
        await Task.Delay(1000);
        s = (await Workspace(id).GetSnapshot())!;

        Assert.Single(s.Conversation, e => e.AuthorKind == ChatAuthorKind.User && e.Text == "status please");
        Assert.Equal("You (via crm)", s.Conversation.Single(e => e.Text == "status please").AuthorName);
        Assert.DoesNotContain(s.Conversation, e => e.Text.Contains("delete everything"));
        Assert.Single(s.Runs);
    }

    [Fact]
    public async Task InvalidCredentials_AreRejected_AndLeaveNoSecretsBehind()
    {
        var id = await CreateWorkspaceAsync();
        var result = await Workspace(id).AddConnection(new ConnectionRequest
        {
            PluginId = "fake-crm",
            Name = "CRM",
            Settings = new() { ["region"] = "eu" },
            Secrets = new() { ["api_key"] = "bad" }
        });

        Assert.False(result.Success);
        Assert.Contains("Invalid API key", result.Message);
        Assert.Empty(await Workspace(id).ListConnections());
        Assert.DoesNotContain(InMemorySecretStore.Values.Keys, k => k.Scope.StartsWith(id));
    }

    [Fact]
    public async Task RemovingAConnection_DeletesItsSecrets_AndItsTools()
    {
        var id = await CreateWorkspaceAsync();
        var c = await ConnectAsync(id);
        Assert.Contains(InMemorySecretStore.Values.Keys, k => k.Scope == $"{id}/{c.ConnectionId}");

        await Workspace(id).RemoveConnection(c.ConnectionId);

        Assert.DoesNotContain(InMemorySecretStore.Values.Keys, k => k.Scope == $"{id}/{c.ConnectionId}");
        Assert.Empty(await Workspace(id).GetConnectionTools());
    }
}
