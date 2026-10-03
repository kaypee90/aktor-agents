using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Task chat: a follow-up from the task's owner reopens a finished root agent with its whole
/// history, a new round of budget and a new deadline, so it builds on its earlier work (and can
/// spawn again) instead of starting over. Only a task's root takes follow-ups.
/// </summary>
public sealed class TaskFollowUpTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static LlmCompletionResponse Respond(params ToolCall[] calls) => new() { ToolCalls = [.. calls], FinishReason = LlmFinishReason.ToolCalls };

    private const string FollowUpText = "Now add a section on pricing.";

    [Fact]
    public async Task A_follow_up_reopens_the_finished_root_with_its_history_and_a_new_round_of_budget()
    {
        var tenant = "org-" + Guid.NewGuid().ToString("n")[..6];
        var taskId = Guid.NewGuid().ToString("n");
        var rootRequests = new ConcurrentQueue<LlmCompletionRequest>();

        ScriptedLlmProviderRegistry.Current = r =>
        {
            var role = PromptInspector.ExtractRole(r.Messages[0].Content!);
            if (!role.Contains("Root", StringComparison.OrdinalIgnoreCase))
            {
                return Respond(Call("complete_task", new { status = "completed", summary = "Pricing researched." }));
            }

            rootRequests.Enqueue(r);
            var followedUp = r.Messages.Any(m => m.Content?.Contains("[Follow-up from the user") == true);
            if (!followedUp)
            {
                // Round one: report, with a stray call after complete_task that must never run.
                return Respond(
                    Call("complete_task", new { status = "completed", summary = "Market report v1." }),
                    Call("spawn_agent", new { role = "Stray", goal = "Should not run", why_not_myself = "n/a" }));
            }

            var spawned = r.Messages.Any(m => m.ToolCalls?.Any(t => t.Name == "spawn_agent" && t.ArgumentsJson.Contains("Pricing")) == true);
            return spawned
                ? Respond(Call("complete_task", new { status = "completed", summary = "Market report v2 with pricing." }))
                : Respond(Call("spawn_agent", new { role = "Pricing Analyst", goal = "Research pricing", why_not_myself = "Needs focused research in parallel." }));
        };

        // A short time budget: by the follow-up it has long run out, so the round needs its own clock.
        var root = await StartRootAsync(tenant, taskId, "Write a market report.", new ResourceBudget { MaxDurationSeconds = 2 });
        var first = await WaitForAsync(root, s => s.Status == AgentStatus.Completed);
        Assert.Equal(AgentStatus.Completed, first.Status);
        Assert.Empty(first.Children);
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        await root.FollowUp(new TaskFollowUp
        {
            Id = "followup-1",
            Text = FollowUpText,
            RoundBudget = new ResourceBudget { MaxDurationSeconds = 60 }
        });

        var second = await WaitForAsync(root, s => s.Status == AgentStatus.Completed && s.FollowUps.Count == 1 && s.Children.Count == 1);
        Assert.Equal(AgentStatus.Completed, second.Status);
        Assert.Equal([FollowUpText], second.FollowUps);
        // It could spawn after reopening: the child got time from the new round, not the old clock.
        Assert.Single(second.Children);
        Assert.Equal(60, second.Budget.MaxDurationSeconds);
        Assert.True(second.StartedExecutionAt > first.CompletedAt);

        // The reopened root saw its whole earlier conversation plus the follow-up, and the follow-up
        // is part of its standing instructions, so compaction can't lose it.
        var reopened = rootRequests.First(r => r.Messages.Any(m => m.Content?.Contains("[Follow-up from the user") == true));
        Assert.Contains(reopened.Messages, m => m.Content?.Contains("Your goal: Write a market report.") == true);
        Assert.Contains(reopened.Messages, m => m.ToolCalls?.Any(t => t.Name == "complete_task" && t.ArgumentsJson.Contains("Market report v1.")) == true);
        Assert.Contains(FollowUpText, reopened.Messages[0].Content);
        // The call queued after complete_task in round one was answered, never run (no child then).
        Assert.Contains(reopened.Messages, m => m.Role == ChatRole.Tool && m.Content?.Contains("already finished") == true);
    }

    [Fact]
    public async Task Only_a_tasks_root_agent_takes_follow_ups()
    {
        ScriptedLlmProviderRegistry.Current = _ => Respond(Call("complete_task", new { status = "completed", summary = "ok" }));
        var childId = $"agent-{Guid.NewGuid():n}"[..14];
        var child = _cluster.GrainFactory.GetGrain<IAgentGrain>(childId);
        await child.Initialize(new AgentInitializationRequest
        {
            AgentId = childId, ParentAgentId = "root-parent", RootAgentId = "root-parent", Name = "Child", Role = "Worker", Goal = "g",
            AllowedTools = ["complete_task"], Budget = new ResourceBudget(), TaskId = Guid.NewGuid().ToString("n"), Depth = 1
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            child.FollowUp(new TaskFollowUp { Id = "followup-x", Text = "Do more", RoundBudget = new ResourceBudget() }));
    }

    private async Task<IAgentGrain> StartRootAsync(string tenant, string taskId, string goal, ResourceBudget budget)
    {
        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        await _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0).RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = goal, RootAgentId = rootId, TenantId = tenant });
        var root = _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId);
        await root.Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = goal,
            AllowedTools = ["spawn_agent", "complete_task"],
            GrantedPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages,
            Budget = budget,
            TaskId = taskId,
            TenantId = tenant,
            AutoStart = true
        });
        return root;
    }

    private static async Task<AgentSnapshot> WaitForAsync(IAgentGrain agent, Func<AgentSnapshot, bool> done, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        AgentSnapshot snapshot;
        while (!done(snapshot = await agent.GetSnapshot()) && DateTime.UtcNow < deadline) await Task.Delay(50);
        return snapshot;
    }
}
