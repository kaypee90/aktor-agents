using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Pipelines;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// The runtime's limits on a pipeline stage's helpers: every spawn needs a reason and a plan, a
/// stage starts no more helpers than it's allowed, helpers can't spawn their own, and a stage with
/// no helpers isn't offered spawning at all.
/// </summary>
public sealed class SpawnDisciplineTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        ScriptedLlmProviderRegistry.Current = Script;
        _cluster = await DurableTestCluster.StartAsync();
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

    private static LlmCompletionResponse Respond(params ToolCall[] calls) =>
        new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    private static ToolCall SpawnHelper(string role) =>
        Call("spawn_agent", new { role, goal = $"{role}: do one part", why_not_myself = "This part runs in parallel with the others." });

    private static ToolCall Plan(string size, int parts) =>
        Call("plan_request", new { parts = Enumerable.Range(1, parts).Select(i => new { title = $"Part {i}", size }).ToArray() });

    private static ToolCall Done() => Call("complete_task", new { status = "completed", summary = "Lead done." });

    /// <summary>The Lead stage plans the run's input, then acts on the plan; helpers just report.</summary>
    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        _requests.Enqueue(r);
        if (r.Messages[0].Content?.Contains("acting as: Lead.") != true)
        {
            return Respond(Call("complete_task", new { status = "completed", summary = "part done" }));
        }

        var kickoff = r.Messages.First(m => m.Role == ChatRole.User).Content ?? string.Empty;
        var input = kickoff.Split("## This run's input", 2)[1].Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
        var called = r.Messages.Where(m => m.Role == ChatRole.Assistant).SelectMany(m => m.ToolCalls ?? []).Select(c => c.Name).ToList();
        var last = r.Messages[^1];

        if (input == "review it")
        {
            if (!called.Contains("plan_request")) return Respond(Plan("large", 1));
            if (!called.Contains("spawn_agent")) return Respond(SpawnHelper("Part E"));
            if (called.Contains("send_message")) return Respond(Done());

            // Once the helper has finished (its notice arrived, or its status says so), ask it for more.
            var spawned = r.Messages.First(m => m.Role == ChatRole.Tool && m.ToolName == "spawn_agent");
            var helperId = JsonDocument.Parse(spawned.Content!).RootElement.GetProperty("agent_id").GetString();
            var finished = last.Content?.Contains("Your child agent") == true ||
                           (last.ToolName == "get_agent_status" && last.Content?.Contains("Completed") == true);
            if (finished) return Respond(Call("send_message", new { to_agent_id = helperId, message_type = "TaskRequest", payload = "Please review your work." }));
            Thread.Sleep(200);
            return Respond(Call("get_agent_status", new { agent_id = helperId }));
        }

        if (called.Count == 0)
        {
            return input switch
            {
                "spawn four" => Respond(Plan("large", 4)),
                "spawn without a reason" => Respond(Plan("large", 2)),
                "small job" => Respond(Plan("small", 2)),
                "spawn without a plan" => Respond(SpawnHelper("Unplanned")),
                _ => Respond(Done())
            };
        }

        if (last.Role == ChatRole.Tool && last.ToolName == "plan_request")
        {
            return input switch
            {
                "spawn four" => Respond(SpawnHelper("Part A"), SpawnHelper("Part B"), SpawnHelper("Part C"), SpawnHelper("Part D")),
                "spawn without a reason" => Respond(Call("spawn_agent", new { role = "Helper", goal = "Help" })),
                "small job" => Respond(SpawnHelper("Needless Helper")),
                _ => Respond(Done())
            };
        }

        return Respond(Done());
    }

    private async Task<string> RunAsync(string input, int maxHelpers = 2)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest
        {
            Name = "Shop",
            Goal = "Help me with my shop.",
            Pipeline = new PipelineDefinition { Stages = [new PipelineStage { StageId = "lead", Name = "Lead", Instructions = "Lead the work.", MaxHelpers = maxHelpers, Retries = 0 }] }
        });
        var run = await Workspace(id).StartRun(input, "user-1");
        Assert.True(run.Success, run.Message);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var view = await _cluster.Client.GetGrain<IPipelineRunGrain>(run.RunId!).GetView();
            if (view?.Status is PipelineRunStatus.Completed or PipelineRunStatus.Failed) return run.RunId!;
            await Task.Delay(150);
        }

        throw new TimeoutException("The run didn't finish.");
    }

    private async Task<List<AgentDirectoryEntry>> HelpersAsync(string runId) =>
        (await _cluster.Client.GetGrain<IAgentRegistryGrain>(0).FindAsync(new FindAgentsQuery { RootAgentId = runId }))
        .Where(a => a.AgentId != runId && a.Role != "Lead").ToList();

    private bool ToolResultSeen(string text) =>
        _requests.Any(r => r.Messages.Any(m => m.Role == ChatRole.Tool && m.Content?.Contains(text) == true));

    [Fact]
    public async Task A_stage_starts_no_more_helpers_than_it_may_and_helpers_cannot_spawn()
    {
        var runId = await RunAsync("spawn four", maxHelpers: 2);

        var helpers = await HelpersAsync(runId);
        Assert.Equal(2, helpers.Count);
        Assert.True(ToolResultSeen("Child-agent budget exhausted"), "the third and fourth are refused");
        Assert.True(ToolResultSeen("shared daily budget"), "the spawner should be told what a helper costs");

        // Helpers do their own job: no spawn tool and no permission to spawn.
        var helper = await _cluster.Client.GetGrain<IAgentGrain>(helpers[0].AgentId).GetSnapshot();
        Assert.DoesNotContain("spawn_agent", helper.AllowedTools);
        Assert.False(helper.GrantedPermissions.HasFlag(ToolPermission.SpawnAgents));
        Assert.Equal(0, helper.Budget.MaxChildren);
    }

    [Fact]
    public async Task A_helper_needs_a_plan_and_a_plan_for_small_work_allows_none()
    {
        var unplanned = await RunAsync("spawn without a plan");
        Assert.True(ToolResultSeen("No workers are planned"));
        Assert.Empty(await HelpersAsync(unplanned));

        // Small parts: the plan says to do it yourself, so a helper is refused.
        var small = await RunAsync("small job");
        Assert.True(ToolResultSeen("\"approach\":\"self\""));
        Assert.Empty(await HelpersAsync(small));
    }

    [Fact]
    public async Task Messaging_a_finished_helper_is_refused_instead_of_waiting_for_a_reply_that_never_comes()
    {
        await RunAsync("review it");
        Assert.True(ToolResultSeen("has finished (Completed)"));
    }

    [Fact]
    public async Task A_spawn_without_a_reason_is_rejected()
    {
        var runId = await RunAsync("spawn without a reason");
        Assert.True(ToolResultSeen("spawn_agent needs why_not_myself"));
        Assert.Empty(await HelpersAsync(runId));
    }

    [Fact]
    public async Task A_stage_without_helpers_is_not_offered_spawning()
    {
        await RunAsync("anything", maxHelpers: 0);
        var lead = _requests.First(r => r.Messages[0].Content?.Contains("acting as: Lead.") == true);
        Assert.DoesNotContain(lead.Tools, t => t.Name is "spawn_agent" or "plan_request");
    }
}
