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

    /// <summary>Each run's input maps to a tool call; the stage reports the tool's result.</summary>
    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        var kickoff = r.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        _llmCallsBy.Enqueue(kickoff.Contains("'Alpha' workspace") ? "alpha" : kickoff.Contains("'Beta' workspace") ? "beta" : "other");

        var last = r.Messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            return last.ToolName == "find_agents" && Input(kickoff) == "twice"
                ? Respond(Call("complete_task", new { status = "completed", summary = "twice done" }))
                : Respond(Call("complete_task", new { status = "completed", summary = $"[{last.ToolName}] {last.Content}" }));
        }

        var text = Input(kickoff);
        string Arg(string prefix) => text[prefix.Length..].Trim();
        if (text.StartsWith("message ")) return Respond(Call("send_message", new { to_agent_id = Arg("message "), message_type = "InformationRequest", payload = "hello from another org" }));
        if (text.StartsWith("status ")) return Respond(Call("get_agent_status", new { agent_id = Arg("status ") }));
        if (text.StartsWith("remember ")) return Respond(Call("write_memory", new { key = "fact", value = Arg("remember "), shared = true }));
        if (text.StartsWith("search ")) return Respond(Call("search_knowledge", new { query = Arg("search ") }));
        if (text.StartsWith("find") || text == "twice") return Respond(Call("find_agents", new { }));
        if (text.StartsWith("spawn")) return Respond(Call("plan_request", new { parts = new[] { new { title = "a", size = "large" }, new { title = "b", size = "large" } } }),
            Call("spawn_agent", new { role = "Helper", goal = "Help out.", capabilities = new[] { "research" }, why_not_myself = "Parallel help." }));
        return Respond(Call("complete_task", new { status = "completed", summary = "stepped" }));
    }

    private static string Input(string kickoff) =>
        kickoff.Split("## This run's input", 2).ElementAtOrDefault(1)?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty;

    private async Task<string> CreateAsync(string name, string tenant)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest
        {
            Name = name,
            Goal = "Help me.",
            TenantId = tenant,
            Pipeline = new Pipelines.PipelineDefinition
            {
                Stages = [new Pipelines.PipelineStage { StageId = "assist", Name = "Assist", Instructions = "Do as the input says.", MaxHelpers = 2, Retries = 0 }]
            }
        });
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

    /// <summary>Runs the pipeline with <paramref name="command"/> and returns the run's result.</summary>
    private async Task<(string RunId, string Result)> AskAsync(string id, string command)
    {
        var started = await Workspace(id).StartRun(command, "user-1");
        Assert.True(started.Success, started.Message);
        var s = await WaitForAsync(id, s => s.Runs.Any(r => r.RunId == started.RunId && r.CompletedAt is not null));
        return (started.RunId!, s.Runs.Single(r => r.RunId == started.RunId).Summary ?? string.Empty);
    }

    [Fact]
    public async Task Agents_CannotReachAnotherOrganizationsAgents_OrItsSharedKnowledge()
    {
        var alpha = await CreateAsync("Alpha", "t-alpha");
        var beta = await CreateAsync("Beta", "t-beta");
        var (betaRun, _) = await AskAsync(beta, "step");

        Assert.Equal("t-alpha", await Workspace(alpha).GetTenantId());
        var (_, sent) = await AskAsync(alpha, $"message {betaRun}");
        Assert.Contains("No such agent", sent);
        Assert.Contains("No such agent", (await AskAsync(alpha, $"status {betaRun}")).Result);
        Assert.DoesNotContain(betaRun, (await AskAsync(alpha, "find")).Result);

        // Beta never heard from Alpha.
        Assert.DoesNotContain((await Workspace(beta).GetSnapshot())!.Conversation, c => c.Text.Contains("hello from another org"));

        // Shared knowledge is shared within an organization only.
        await AskAsync(alpha, "remember the launch code is 1234");
        Assert.Contains("1234", (await AskAsync(alpha, "search launch")).Result);
        Assert.DoesNotContain("1234", (await AskAsync(beta, "search launch")).Result);
    }

    [Fact]
    public async Task Usage_IsMeteredPerOrganization()
    {
        var alpha = await CreateAsync("Alpha", "t-meter-a");
        await AskAsync(alpha, "twice");
        await CreateAsync("Beta", "t-meter-b");

        var a = await Tenant("t-meter-a").GetUsage();
        var b = await Tenant("t-meter-b").GetUsage();
        var alphaCalls = _llmCallsBy.Count(x => x == "alpha");
        Assert.Equal(2, alphaCalls);
        Assert.Equal(alphaCalls, a.Current.LlmCalls);
        Assert.Equal(alphaCalls * 600, a.Current.Tokens);
        Assert.True(a.Current.ToolCalls >= 2);
        Assert.Equal(1, a.Current.AgentsCreated);
        Assert.Equal(0, b.Current.LlmCalls);
        Assert.Equal(TenantUsagePeriod.PeriodOf(DateTimeOffset.UtcNow), a.Current.Period);
    }

    [Fact]
    public async Task OverQuota_AgentsPause_WithoutCallingTheModel_AndResumeWhenThePlanChanges()
    {
        await Tenant("t-quota").SetBilling(new TenantBillingState { PlanId = "tiny" });
        var alpha = await CreateAsync("Alpha", "t-quota");
        await AskAsync(alpha, "twice"); // two calls: 1,200 tokens >= 1,000, so the next is refused
        await Workspace(alpha).StartRun("step", "user-1");

        var s = await WaitForAsync(alpha, s => s.Agents.Any(a => a.CurrentTask?.StartsWith("Paused:") == true));
        var parked = s.Agents.Single(a => a.CurrentTask?.StartsWith("Paused:") == true);
        Assert.Contains("Tiny plan", parked.CurrentTask);

        var calls = _llmCallsBy.Count;
        await Task.Delay(3000); // recovery reminders re-check (every 2s here) and stay parked
        Assert.Equal(calls, _llmCallsBy.Count);
        var usage = await Tenant("t-quota").GetUsage();
        Assert.False(usage.Quota.Allowed);
        Assert.Equal(1, usage.ParkedAgents);

        await Tenant("t-quota").SetBilling(new TenantBillingState { PlanId = "unlimited", SubscriptionStatus = "active" });
        s = await WaitForAsync(alpha, s => s.Runs.All(r => r.CompletedAt is not null));
        Assert.True(_llmCallsBy.Count > calls);
        Assert.Equal(0, (await Tenant("t-quota").GetUsage()).ParkedAgents);
    }

    [Fact]
    public async Task PlanActiveAgentLimit_RefusesSpawns()
    {
        await Tenant("t-solo").SetBilling(new TenantBillingState { PlanId = "solo" });
        var alpha = await CreateAsync("Alpha", "t-solo");
        var (runId, result) = await AskAsync(alpha, "spawn");
        // The stage's agent is the one active agent the plan allows: its run doesn't count, its helper is refused.
        Assert.Contains("plan allows 1 active agents", result);
        var team = await _cluster.Client.GetGrain<IAgentRegistryGrain>(0).FindAsync(new AgentRuntime.Contracts.FindAgentsQuery { RootAgentId = runId });
        Assert.Equal(2, team.Count); // the run and its stage
    }
}
