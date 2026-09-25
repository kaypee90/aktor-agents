using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.Integrations;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Safety;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Phase 5 end to end: the workspace safety policy gates tool calls in the runtime, approvals park
/// the agent durably (through stops and crashes), decisions come from the API, chat or an inbound
/// channel, and everything lands in a hash-chained audit log.
/// </summary>
public sealed class SafetyTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
        FakeCrmPlugin.Reset();
        InMemorySecretStore.Values.Clear();
        _cluster = await DurableTestCluster.StartAsync();
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

    private static LlmCompletionResponse Respond(string? content, params ToolCall[] calls) =>
        new() { Content = content, ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        _requests.Enqueue(r);
        var last = r.Messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            return last.ToolName is "crm__delete_customer" or "crm__lookup_customer"
                ? Respond(null, Call("notify_user", new { text = $"Result of {last.ToolName}: {last.Content}" }))
                : Respond(null, Call("wait_for_events", new { summary = "Done." }));
        }

        var text = (last.Content ?? string.Empty).Split('\n').Last();
        if (text.Contains("delete customer 7")) return Respond("Removing the duplicate record.", Call("crm__delete_customer", new { id = "7" }));
        if (text.Contains("look up customer 42")) return Respond(null, Call("crm__lookup_customer", new { id = "42" }));
        return Respond(null, Call("wait_for_events", new { summary = "Ready." }));
    }

    private async Task<string> CreateWorkspaceAsync(WorkspaceSafetyPolicy policy, List<string>? senders = null)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = "CRM", Goal = "Help me with my customers." });
        await WaitForAsync(id, s => s.Agents.Any(a => a.Status == "Waiting"));
        var result = await Workspace(id).AddConnection(new ConnectionRequest
        {
            PluginId = "fake-crm",
            Name = "CRM",
            Settings = new() { ["region"] = "eu" },
            Secrets = new() { ["api_key"] = "k" },
            NotifyLevel = NotifyLevel.Off,
            AllowedSenders = senders ?? []
        });
        Assert.True(result.Success, result.Message);
        await Workspace(id).UpdateSafetyPolicy(policy, "user");
        return id;
    }

    private async Task<WorkspaceSnapshot> WaitForAsync(string id, Func<WorkspaceSnapshot, bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        WorkspaceSnapshot? s = null;
        while (DateTime.UtcNow < deadline)
        {
            try { s = await Workspace(id).GetSnapshot(); }
            catch (Exception) when (DateTime.UtcNow < deadline) { await Task.Delay(300); continue; } // the client reconnecting after a crash
            if (s is not null && condition(s)) return s;
            await Task.Delay(150);
        }

        throw new TimeoutException("Condition not met. Conversation:\n" + string.Join("\n", s?.Conversation.Select(c => $"[{c.AuthorName}] {c.Text}") ?? []));
    }

    private static int Deletes => FakeCrmPlugin.ToolCalls.Count(c => c.Tool == "delete_customer");

    private async Task<ApprovalRecord> RequestDeleteAsync(string id)
    {
        await Workspace(id).PostUserMessage("delete customer 7", null, null);
        var s = await WaitForAsync(id, s => s.Approvals.Any(a => a.Status == ApprovalStatus.Pending) &&
                                            s.Agents.Any(a => a.CurrentTask?.StartsWith("Waiting for approval") == true));
        return s.Approvals.Single();
    }

    private static readonly WorkspaceSafetyPolicy SemiAutonomous = new() { Autonomy = AutonomyLevel.SemiAutonomous };

    [Fact]
    public async Task SemiAutonomous_ParksAnExternalWrite_UntilApproved_ThenRunsItOnce_AndAuditsEverything()
    {
        var id = await CreateWorkspaceAsync(SemiAutonomous);
        var approval = await RequestDeleteAsync(id);

        Assert.Equal("A1", approval.Code);
        Assert.Equal("crm__delete_customer", approval.ToolName);
        Assert.Equal("Removing the duplicate record.", approval.AgentNote);
        Assert.Contains("\"7\"", approval.ArgumentsJson);
        await Task.Delay(2500); // a recovery reminder re-checks the parked call: still no run
        Assert.Equal(0, Deletes);
        Assert.Contains((await Workspace(id).GetSnapshot())!.Conversation, c => c.Text.StartsWith("Approval A1 needed"));

        var decision = await Workspace(id).DecideApproval(approval.ApprovalId, approve: true, null, "user", "api");
        Assert.True(decision.Success, decision.Message);
        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.StartsWith("Result of crm__delete_customer")));
        await Task.Delay(1000);

        Assert.Equal(1, Deletes);
        Assert.Equal(ApprovalStatus.Approved, s.Approvals.Single().Status);
        Assert.False((await Workspace(id).DecideApproval("A1", approve: false, null, "user", "api")).Success); // decided once

        var audit = await TestAudit.Log.QueryAsync(new AuditQuery { Scope = id });
        var actions = audit.Select(a => a.Action).ToList();
        Assert.Contains("policy.updated", actions);
        Assert.Contains("approval.requested", actions);
        Assert.Contains("approval.approved", actions);
        var run = Assert.Single(audit, a => a.Action == "tool.call" && a.Target == "crm__delete_customer");
        Assert.Equal("ok", run.Outcome);
        Assert.Equal("NonIdempotent", run.SideEffects);
        Assert.Contains("duplicate record", run.DetailJson);
        Assert.True((await TestAudit.Log.VerifyAsync(id)).Valid);
    }

    [Fact]
    public async Task Rejection_FromAnAllowedSender_ReachesTheAgentAsAnError_AndNothingRuns()
    {
        var id = await CreateWorkspaceAsync(SemiAutonomous, senders: ["+15550001111"]);
        await RequestDeleteAsync(id);
        var connection = (await Workspace(id).ListConnections()).Single();
        var token = connection.InboundPath!.Split('/').Last();

        // A stranger can't approve.
        await Workspace(id).HandleInbound(connection.ConnectionId, token, new InboundRequestDto { Body = "+19999999999|m1|approve A1" });
        await Task.Delay(500);
        Assert.Equal(ApprovalStatus.Pending, (await Workspace(id).GetSnapshot())!.Approvals.Single().Status);

        await Workspace(id).HandleInbound(connection.ConnectionId, token, new InboundRequestDto { Body = "+15550001111|m2|reject A1 keep that record" });
        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.StartsWith("Result of crm__delete_customer")));

        Assert.Equal(0, Deletes);
        var approval = s.Approvals.Single();
        Assert.Equal(ApprovalStatus.Rejected, approval.Status);
        Assert.Equal("+15550001111", approval.DecidedBy);
        Assert.Equal("keep that record", approval.DecisionReason);
        Assert.Contains("rejected", s.Conversation.Last(c => c.Text.StartsWith("Result of")).Text);
        // Decisions from channels aren't forwarded to the agents as commands.
        Assert.DoesNotContain(s.Conversation, c => c.AuthorKind == ChatAuthorKind.User && c.Text.Contains("A1"));
        Assert.DoesNotContain(_requests, r => r.Messages.Any(m => m.Content?.Contains("keep that record") == true && m.Role == ChatRole.User));
    }

    [Fact]
    public async Task ApproveTypedInTheChat_DecidesTheApproval_WithoutReachingTheAgent()
    {
        var id = await CreateWorkspaceAsync(SemiAutonomous);
        await RequestDeleteAsync(id);

        await Workspace(id).PostUserMessage("approve a1", null, null);
        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.StartsWith("Result of crm__delete_customer")));

        Assert.Equal(1, Deletes);
        Assert.DoesNotContain(_requests, r => r.Messages.Any(m => m.Content?.Contains("approve a1") == true));
        Assert.Equal("user", s.Approvals.Single().DecidedBy);
    }

    [Fact]
    public async Task DenyRule_BlocksWithoutAskingForApproval_AndReadsStayAllowedWhenSupervised()
    {
        var id = await CreateWorkspaceAsync(new WorkspaceSafetyPolicy
        {
            Autonomy = AutonomyLevel.Supervised,
            Rules = [new ApprovalRule { Name = "no deletes", ToolPattern = "*__delete_*", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Deny }]
        });

        await Workspace(id).PostUserMessage("delete customer 7", null, null);
        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.StartsWith("Result of crm__delete_customer")));
        var blocked = s.Conversation.Last(c => c.Text.StartsWith("Result of")).Text;
        Assert.True(blocked.Contains("Blocked by the workspace"), blocked);
        Assert.Empty(s.Approvals);

        await Workspace(id).PostUserMessage("look up customer 42", null, null);
        await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.StartsWith("Result of crm__lookup_customer")));

        Assert.Equal(0, Deletes);
        Assert.Single(FakeCrmPlugin.ToolCalls, c => c.Tool == "lookup_customer");
        var audit = await TestAudit.Log.QueryAsync(new AuditQuery { Scope = id });
        Assert.Single(audit, a => a.Action == "tool.denied" && a.Target == "crm__delete_customer");
        Assert.Single(audit, a => a.Action == "tool.call" && a.Target == "crm__lookup_customer");
    }

    [Fact]
    public async Task AParkedAgent_CanStillBeStopped_AndALaterApprovalRunsNothing()
    {
        var id = await CreateWorkspaceAsync(SemiAutonomous);
        var approval = await RequestDeleteAsync(id);

        var agent = _cluster.Client.GetGrain<IAgentGrain>(approval.AgentId);
        await agent.Stop();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await agent.GetStatus() != AgentStatus.Terminated && DateTime.UtcNow < deadline) await Task.Delay(150);
        Assert.Equal(AgentStatus.Terminated, await agent.GetStatus());

        await Workspace(id).DecideApproval("A1", approve: true, null, "user", "api");
        await Task.Delay(2500);
        Assert.Equal(0, Deletes);
    }

    [Fact]
    public async Task APendingApproval_SurvivesACrash_AndApprovingAfterwardsRunsTheCallOnce()
    {
        var id = await CreateWorkspaceAsync(SemiAutonomous);
        await RequestDeleteAsync(id);

        await _cluster.CrashAndRestartAsync();
        await Task.Delay(3000); // recovery reminders fire and re-park the call
        var s = await WaitForAsync(id, _ => true);
        Assert.Equal(ApprovalStatus.Pending, s.Approvals.Single().Status);
        Assert.Single(s.Approvals); // re-checking never creates a second request
        Assert.Equal(0, Deletes);

        await Workspace(id).DecideApproval("A1", approve: true, null, "user", "api");
        await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.StartsWith("Result of crm__delete_customer")));
        await Task.Delay(1000);
        Assert.Equal(1, Deletes);
    }
}
