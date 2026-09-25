using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Tenancy;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Phase 6: organizations are isolated by the runtime itself (messaging, discovery, status,
/// shared memory), usage is metered per organization, and plan quotas pause agents until the
/// plan changes.
/// </summary>
public sealed class TenancyTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<string> _llmCallsBy = new();

    public async Task InitializeAsync()
    {
        _cluster = await DurableTestCluster.StartAsync(new Dictionary<string, string?>
        {
            ["Billing:DefaultPlan"] = "unlimited",
            ["Billing:Plans:0:Id"] = "tiny",
            ["Billing:Plans:0:Name"] = "Tiny",
            ["Billing:Plans:0:MonthlyTokenLimit"] = "1000",
            ["Billing:Plans:1:Id"] = "solo",
            ["Billing:Plans:1:Name"] = "Solo",
            ["Billing:Plans:1:MaxActiveAgents"] = "1"
        });
        ScriptedLlmProviderRegistry.Current = Script;
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.DisposeAsync();
    }

    private IWorkspaceGrain Workspace(string id) => _cluster.Client.GetGrain<IWorkspaceGrain>(id);
    private ITenantGrain Tenant(string id) => _cluster.Client.GetGrain<ITenantGrain>(id);

    private static ToolCall Call(string name, object? args = null) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args ?? new { })
    };

    private static LlmCompletionResponse Respond(params ToolCall[] calls) =>
        new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls, InputTokens = 500, OutputTokens = 100 };

    /// <summary>User commands map to tool calls; every tool result is reported back with notify_user.</summary>
    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        var system = r.Messages[0].Content ?? string.Empty;
        _llmCallsBy.Enqueue(system.Contains("'Alpha'") ? "alpha" : system.Contains("'Beta'") ? "beta" : "other");

        var last = r.Messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            return last.ToolName == "notify_user"
                ? Respond(Call("wait_for_events", new { summary = "done" }))
                : Respond(Call("notify_user", new { text = $"[{last.ToolName}] {last.Content}" }));
        }

        var text = (last.Content ?? string.Empty).Split('\n').Last();
        string Arg(string prefix) => text[prefix.Length..].Trim();
        if (text.StartsWith("message ")) return Respond(Call("send_message", new { to_agent_id = Arg("message "), message_type = "InformationRequest", payload = "hello from another org" }));
        if (text.StartsWith("status ")) return Respond(Call("get_agent_status", new { agent_id = Arg("status ") }));
        if (text.StartsWith("remember ")) return Respond(Call("write_memory", new { key = "fact", value = Arg("remember "), shared = true }));
        if (text.StartsWith("search ")) return Respond(Call("search_knowledge", new { query = Arg("search ") }));
        if (text.StartsWith("find")) return Respond(Call("find_agents", new { }));
        if (text.StartsWith("spawn")) return Respond(Call("spawn_agent", new { role = "Helper", goal = "Help out.", capabilities = new[] { "research" }, standing = true }));
        if (text.StartsWith("step")) return Respond(Call("notify_user", new { text = "stepped" }));
        return Respond(Call("wait_for_events", new { summary = "ready" }));
    }

    private async Task<string> CreateAsync(string name, string tenant)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = name, Goal = "Help me.", TenantId = tenant });
        await WaitForAsync(id, s => s.Agents.Any(a => a.Status == "Waiting"));
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

    private async Task<string> AskAsync(string id, string command, string tool)
    {
        var before = (await Workspace(id).GetSnapshot())!.Conversation.Count(c => c.Text.StartsWith($"[{tool}]"));
        await Workspace(id).PostUserMessage(command, null, null);
        var s = await WaitForAsync(id, s => s.Conversation.Count(c => c.Text.StartsWith($"[{tool}]")) > before);
        return s.Conversation.Last(c => c.Text.StartsWith($"[{tool}]")).Text;
    }

    [Fact]
    public async Task Agents_CannotReachAnotherOrganizationsAgents_OrItsSharedKnowledge()
    {
        var alpha = await CreateAsync("Alpha", "t-alpha");
        var beta = await CreateAsync("Beta", "t-beta");
        var betaCoordinator = WorkspaceIds.CoordinatorId(beta);
        var alphaCoordinator = WorkspaceIds.CoordinatorId(alpha);

        Assert.Equal("t-alpha", await Workspace(alpha).GetTenantId());
        Assert.Equal("t-alpha", (await _cluster.Client.GetGrain<IAgentGrain>(alphaCoordinator).GetSnapshot()).TenantId);

        var sent = await AskAsync(alpha, $"message {betaCoordinator}", "send_message");
        Assert.Contains("No such agent", sent);
        Assert.Contains("No such agent", await AskAsync(alpha, $"status {betaCoordinator}", "get_agent_status"));
        Assert.DoesNotContain(betaCoordinator, await AskAsync(alpha, "find", "find_agents"));

        // Beta never heard from Alpha.
        var betaState = await _cluster.Client.GetGrain<IAgentGrain>(betaCoordinator).GetSnapshot();
        Assert.DoesNotContain((await Workspace(beta).GetSnapshot())!.Conversation, c => c.Text.Contains("hello from another org"));

        // Shared knowledge is shared within an organization only.
        await AskAsync(alpha, "remember the launch code is 1234", "write_memory");
        Assert.Contains("1234", await AskAsync(alpha, "search launch", "search_knowledge"));
        Assert.DoesNotContain("1234", await AskAsync(beta, "search launch", "search_knowledge"));
        Assert.Equal(AgentRuntime.Contracts.AgentStatus.Waiting, betaState.Status);
    }

    [Fact]
    public async Task Usage_IsMeteredPerOrganization()
    {
        var alpha = await CreateAsync("Alpha", "t-meter-a");
        await Workspace(alpha).PostUserMessage("step", null, null);
        await WaitForAsync(alpha, s => s.Conversation.Any(c => c.Text == "stepped") && s.Agents.All(a => a.Status == "Waiting"));
        await CreateAsync("Beta", "t-meter-b");

        var a = await Tenant("t-meter-a").GetUsage();
        var b = await Tenant("t-meter-b").GetUsage();
        var alphaCalls = _llmCallsBy.Count(x => x == "alpha");
        Assert.Equal(alphaCalls, a.Current.LlmCalls);
        Assert.Equal(alphaCalls * 600, a.Current.Tokens);
        Assert.True(a.Current.ToolCalls >= 2);
        Assert.Equal(1, a.Current.AgentsCreated);
        Assert.Equal(_llmCallsBy.Count(x => x == "beta"), b.Current.LlmCalls);
        Assert.Equal(TenantUsagePeriod.PeriodOf(DateTimeOffset.UtcNow), a.Current.Period);
    }

    [Fact]
    public async Task OverQuota_AgentsPause_WithoutCallingTheModel_AndResumeWhenThePlanChanges()
    {
        await Tenant("t-quota").SetBilling(new TenantBillingState { PlanId = "tiny" });
        var alpha = await CreateAsync("Alpha", "t-quota"); // 1 call: 600 tokens
        await Workspace(alpha).PostUserMessage("step", null, null); // 2nd call: 1200 >= 1000, then the next is refused

        var coordinator = _cluster.Client.GetGrain<IAgentGrain>(WorkspaceIds.CoordinatorId(alpha));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await coordinator.GetSnapshot()).CurrentTask?.StartsWith("Paused:") != true && DateTime.UtcNow < deadline) await Task.Delay(150);
        var parked = await coordinator.GetSnapshot();
        Assert.StartsWith("Paused:", parked.CurrentTask);
        Assert.Contains("Tiny plan", parked.CurrentTask);

        var calls = _llmCallsBy.Count;
        await Task.Delay(3000); // recovery reminders re-check (every 2s here) and stay parked
        Assert.Equal(calls, _llmCallsBy.Count);
        var usage = await Tenant("t-quota").GetUsage();
        Assert.False(usage.Quota.Allowed);
        Assert.Equal(1, usage.ParkedAgents);

        await Tenant("t-quota").SetBilling(new TenantBillingState { PlanId = "unlimited", SubscriptionStatus = "active" });
        var resumeDeadline = DateTime.UtcNow.AddSeconds(20);
        while (_llmCallsBy.Count == calls && DateTime.UtcNow < resumeDeadline) await Task.Delay(150);
        Assert.True(_llmCallsBy.Count > calls);
        await Task.Delay(500);
        Assert.DoesNotContain("Paused:", (await coordinator.GetSnapshot()).CurrentTask ?? string.Empty);
        Assert.Equal(0, (await Tenant("t-quota").GetUsage()).ParkedAgents);
    }

    [Fact]
    public async Task PlanActiveAgentLimit_RefusesSpawns()
    {
        await Tenant("t-solo").SetBilling(new TenantBillingState { PlanId = "solo" });
        var alpha = await CreateAsync("Alpha", "t-solo");
        var result = await AskAsync(alpha, "spawn", "spawn_agent");
        Assert.Contains("plan allows 1 active agents", result);
        Assert.Single((await Workspace(alpha).GetSnapshot())!.Agents);
    }
}
