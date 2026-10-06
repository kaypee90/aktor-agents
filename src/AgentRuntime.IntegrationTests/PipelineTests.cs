using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Pipelines;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Workspace pipelines end to end with a scripted LLM (docs/workspaces.md): stages run in
/// dependency order with their inputs' results, parallel branches merge, failures retry and then
/// fail the run or carry on, triggers (webhooks, schedules) start runs, the daily budget stops
/// runs, pause queues them, and edits are versioned.
/// </summary>
public sealed class PipelineTests : IAsyncLifetime
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
        ScriptedLlmProviderRegistry.CurrentAsync = null;
        await _cluster.DisposeAsync();
    }

    private IWorkspaceGrain Workspace(string id) => _cluster.Client.GetGrain<IWorkspaceGrain>(id);
    private IPipelineRunGrain Run(string runId) => _cluster.Client.GetGrain<IPipelineRunGrain>(runId);

    private static PipelineStage Stage(string id, params string[] inputs) =>
        new() { StageId = id, Name = id, Role = $"{id} agent", Instructions = $"Do the {id} part.", Inputs = [.. inputs], Retries = 0 };

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static string RoleOf(LlmCompletionRequest r) =>
        PromptInspector.ExtractRole(r.Messages[0].Content!).Replace(" agent", string.Empty);

    private static string Kickoff(LlmCompletionRequest r) =>
        r.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;

    /// <summary>Every stage reports "&lt;stage&gt; did: &lt;the run's input&gt;"; <paramref name="behave"/>
    /// can answer differently for some stages.</summary>
    private Func<LlmCompletionRequest, LlmCompletionResponse> Script(int tokensPerCall = 0,
        Func<string, LlmCompletionRequest, LlmCompletionResponse?>? behave = null) => r =>
    {
        _requests.Enqueue(r);
        var stage = RoleOf(r);
        if (behave?.Invoke(stage, r) is { } special) return special with { InputTokens = tokensPerCall };
        var input = Kickoff(r).Split("## This run's input", 2).ElementAtOrDefault(1)?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "?";
        return new LlmCompletionResponse
        {
            ToolCalls = [Call("complete_task", new { status = "completed", summary = $"{stage} did: {input}" })],
            FinishReason = LlmFinishReason.ToolCalls,
            InputTokens = tokensPerCall
        };
    };

    private async Task<string> CreateAsync(PipelineDefinition pipeline, int? dailyTokens = null)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = "Research desk", Goal = "Research topics.", Pipeline = pipeline, DailyTokenLimit = dailyTokens });
        return id;
    }

    private async Task<PipelineRunView> WaitForRunAsync(string runId, Func<PipelineRunView, bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        PipelineRunView? view = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                view = await Run(runId).GetView();
                if (view is not null && condition(view)) return view;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                // Cluster recovering.
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"Run condition not met. Run: {JsonSerializer.Serialize(view)}");
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

    private static bool Done(PipelineRunView r) => r.Status is not (PipelineRunStatus.Queued or PipelineRunStatus.Running);

    [Fact]
    public async Task A_run_goes_stage_by_stage_each_stage_gets_its_inputs_results_and_the_output_is_the_runs_result()
    {
        ScriptedLlmProviderRegistry.Current = Script();
        var id = await CreateAsync(new PipelineDefinition { Stages = [Stage("research"), Stage("write", "research"), Stage("review", "write")] });

        var started = await Workspace(id).StartRun("solar panels", "user-1");
        Assert.True(started.Success, started.Message);
        var run = await WaitForRunAsync(started.RunId!, Done);

        Assert.Equal(PipelineRunStatus.Completed, run.Status);
        Assert.All(run.Stages, s => Assert.Equal(StageRunStatus.Completed, s.Status));
        Assert.True(run.Stages[0].CompletedAt <= run.Stages[1].StartedAt && run.Stages[1].CompletedAt <= run.Stages[2].StartedAt, "in order");
        Assert.Equal("review did: solar panels", run.Summary);

        // Each stage saw the run's input and only its own inputs' results.
        var write = _requests.First(r => RoleOf(r) == "write");
        Assert.Contains("research did: solar panels", Kickoff(write));
        var review = _requests.First(r => RoleOf(r) == "review");
        Assert.Contains("write did: solar panels", Kickoff(review));
        Assert.DoesNotContain("research did", Kickoff(review));

        // The workspace recorded it and told the user.
        var s = await WaitForAsync(id, s => s.Runs.Any(r => r.RunId == run.RunId && r.Status == PipelineRunStatus.Completed));
        Assert.Contains(s.Conversation, c => c.Text.Contains("Run #1 finished") && c.Text.Contains("review did: solar panels"));
        Assert.Equal("user-1", s.Runs.Single().StartedBy);
    }

    [Fact]
    public async Task Parallel_branches_run_together_and_the_stage_that_merges_them_waits_for_both()
    {
        var bothRunning = new TaskCompletionSource();
        var running = 0;
        ScriptedLlmProviderRegistry.Current = Script(behave: (stage, _) =>
        {
            if (stage is not ("market" or "tech")) return null;
            // Each branch holds until the other has started: proof they run at the same time.
            if (Interlocked.Increment(ref running) == 2) bothRunning.TrySetResult();
            bothRunning.Task.Wait(TimeSpan.FromSeconds(10));
            return null;
        });
        var id = await CreateAsync(new PipelineDefinition
        {
            Stages = [Stage("brief"), Stage("market", "brief"), Stage("tech", "brief"), Stage("report", "market", "tech")]
        });

        var run = await WaitForRunAsync((await Workspace(id).StartRun("a drone startup", "user-1")).RunId!, Done);

        Assert.True(bothRunning.Task.IsCompleted, "market and tech ran in parallel");
        Assert.Equal(PipelineRunStatus.Completed, run.Status);
        var report = _requests.First(r => RoleOf(r) == "report");
        Assert.Contains("market did: a drone startup", Kickoff(report));
        Assert.Contains("tech did: a drone startup", Kickoff(report));
        var stages = run.Stages.ToDictionary(s => s.StageId);
        Assert.True(stages["report"].StartedAt >= stages["market"].CompletedAt && stages["report"].StartedAt >= stages["tech"].CompletedAt);
    }

    [Fact]
    public async Task A_failing_stage_is_retried_then_fails_the_run_and_later_stages_are_skipped()
    {
        ScriptedLlmProviderRegistry.Current = Script(behave: (stage, _) => stage == "write"
            ? new LlmCompletionResponse { Content = "I refuse.", FinishReason = LlmFinishReason.Stop } // no tool call: nudged, then fails
            : null);
        var id = await CreateAsync(new PipelineDefinition
        {
            Stages = [Stage("research"), Stage("write", "research") with { Retries = 1 }, Stage("review", "write")]
        });

        var run = await WaitForRunAsync((await Workspace(id).StartRun("anything", "user-1")).RunId!, Done, 60);

        Assert.Equal(PipelineRunStatus.Failed, run.Status);
        var stages = run.Stages.ToDictionary(s => s.StageId);
        Assert.Equal(StageRunStatus.Failed, stages["write"].Status);
        Assert.Equal(2, stages["write"].Attempts);
        Assert.Equal(StageRunStatus.Skipped, stages["review"].Status);
        Assert.DoesNotContain(_requests, r => RoleOf(r) == "review");
    }

    [Fact]
    public async Task With_continue_on_failure_later_stages_still_run_and_are_told()
    {
        ScriptedLlmProviderRegistry.Current = Script(behave: (stage, _) => stage == "metrics"
            ? new LlmCompletionResponse { Content = "No.", FinishReason = LlmFinishReason.Stop }
            : null);
        var id = await CreateAsync(new PipelineDefinition
        {
            Stages = [Stage("logs"), Stage("metrics") with { OnFailure = StageFailurePolicy.Continue }, Stage("diagnose", "logs", "metrics")]
        });

        var run = await WaitForRunAsync((await Workspace(id).StartRun("checkout errors", "user-1")).RunId!, Done, 60);

        Assert.Equal(PipelineRunStatus.Completed, run.Status);
        Assert.Equal(StageRunStatus.Failed, run.Stages.Single(s => s.StageId == "metrics").Status);
        Assert.Contains("(FAILED)", Kickoff(_requests.First(r => RoleOf(r) == "diagnose")));
    }

    [Fact]
    public async Task Webhooks_start_one_run_per_delivery_authenticated_deduplicated_rate_limited_and_secret()
    {
        ScriptedLlmProviderRegistry.Current = Script();
        var id = await CreateAsync(new PipelineDefinition { Stages = [Stage("triage")] });

        var created = await Workspace(id).AddTrigger(new TriggerSpec { Kind = TriggerKind.Webhook, Name = "Tickets", Instruction = "Triage this ticket." },
            "user", string.Empty, revealSecret: true);
        Assert.True(created.Success, created.Message);
        var view = JsonSerializer.Deserialize<JsonElement>(created.ResultJson!);
        var triggerId = view.GetProperty("trigger_id").GetString()!;
        var secret = view.GetProperty("webhook_path").GetString()!.Split('/').Last();
        WebhookDelivery Delivery(string body, string id, string? token = null) => new() { TriggerId = triggerId, Token = token ?? secret, Body = body, DeliveryId = id };

        Assert.Equal(WebhookOutcome.Unauthorized, await Workspace(id).DeliverWebhook(Delivery("{}", "d0", token: "wrong")));
        Assert.Equal(WebhookOutcome.Accepted, await Workspace(id).DeliverWebhook(Delivery("""{"ticket":"A1"}""", "d1")));
        Assert.Equal(WebhookOutcome.Duplicate, await Workspace(id).DeliverWebhook(Delivery("""{"ticket":"A1"}""", "d1")));
        Assert.Equal(WebhookOutcome.Accepted, await Workspace(id).DeliverWebhook(Delivery("""{"ticket":"B2"}""", "d2")));
        Assert.Equal(WebhookOutcome.Accepted, await Workspace(id).DeliverWebhook(Delivery("""{"ticket":"C3"}""", "d3")));
        Assert.Equal(WebhookOutcome.RateLimited, await Workspace(id).DeliverWebhook(Delivery("""{"ticket":"D4"}""", "d4")));

        var s = await WaitForAsync(id, s => s.Runs.Count(r => r.Status == PipelineRunStatus.Completed) == 3);
        Assert.All(s.Runs, r => Assert.Equal("webhook", r.Source));
        Assert.All(s.Runs, r => Assert.Equal("trigger", r.StartedBy));
        Assert.Single(s.Runs, r => r.Input.Contains("A1"));
        Assert.DoesNotContain(s.Runs, r => r.Input.Contains("D4"));
        Assert.Equal(1, s.Triggers.Single().DroppedCount);
        // The user saw the secret URL in chat; no agent ever did.
        Assert.Contains(s.Conversation, c => c.Text.Contains(secret));
        Assert.DoesNotContain(_requests, r => r.Messages.Any(m => m.Content?.Contains(secret) == true));
    }

    [Fact]
    public async Task A_schedule_runs_the_pipeline_and_keeps_firing_after_a_crash()
    {
        ScriptedLlmProviderRegistry.Current = Script();
        var id = await CreateAsync(new PipelineDefinition { Stages = [Stage("brief")] });
        Assert.True((await Workspace(id).AddTrigger(new TriggerSpec { Kind = TriggerKind.Schedule, Name = "Every second", Instruction = "Morning brief.", EveryMinutes = 1.0 / 60 },
            "user", string.Empty, revealSecret: false)).Success);

        var before = await WaitForAsync(id, s => s.Runs.Count(r => r.Status == PipelineRunStatus.Completed) >= 1);
        await _cluster.CrashAndRestartAsync();
        await Task.Delay(TimeSpan.FromSeconds(5)); // the reminders, not our polling, must revive it

        var after = await WaitForAsync(id, s => s.Runs.Count(r => r.Status == PipelineRunStatus.Completed) >= before.Runs.Count + 2, 60);
        Assert.All(after.Runs, r => Assert.Equal("schedule", r.Source));
    }

    [Fact]
    public async Task The_daily_budget_stops_runs_once_used_up_and_tells_the_user_once()
    {
        // 3,000 tokens a call against a 10,000-token day.
        ScriptedLlmProviderRegistry.Current = Script(tokensPerCall: 3_000);
        var id = await CreateAsync(new PipelineDefinition { Stages = [Stage("a"), Stage("b", "a"), Stage("c", "b"), Stage("d", "c"), Stage("e", "d")] }, dailyTokens: 10_000);

        await Workspace(id).StartRun("go", "user-1");
        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.Contains("Agents are paused for today")));
        var callsWhenStopped = _requests.Count;
        await Task.Delay(3000);

        s = (await Workspace(id).GetSnapshot())!;
        Assert.InRange(_requests.Count - callsWhenStopped, 0, 1); // at most one call already in flight
        Assert.Equal(1, s.Conversation.Count(c => c.Text.Contains("Agents are paused for today")));
        Assert.True(s.TokensToday >= 10_000);
    }

    [Fact]
    public async Task Runs_beyond_the_limit_wait_in_a_queue_and_pause_holds_them_until_resume()
    {
        // Stages wait (without holding a thread) until released, so the first run is still going when
        // the second is asked for and when the workspace is paused.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = Script();
        ScriptedLlmProviderRegistry.CurrentAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            return script(request);
        };
        var id = await CreateAsync(new PipelineDefinition { Stages = [Stage("only")], MaxConcurrentRuns = 1 });

        var first = await Workspace(id).StartRun("first", "user-1");
        var second = await Workspace(id).StartRun("second", "user-1");
        Assert.Equal("started", first.Message);
        Assert.Equal("queued", second.Message);
        Assert.Equal(PipelineRunStatus.Queued, (await Run(second.RunId!).GetView())!.Status);

        await Workspace(id).Pause();
        release.TrySetResult();
        await WaitForRunAsync(first.RunId!, r => r.Paused || Done(r));
        await Task.Delay(1500);
        Assert.Equal(PipelineRunStatus.Queued, (await Run(second.RunId!).GetView())!.Status);

        await Workspace(id).Resume();
        Assert.Equal(PipelineRunStatus.Completed, (await WaitForRunAsync(first.RunId!, Done)).Status);
        Assert.Equal(PipelineRunStatus.Completed, (await WaitForRunAsync(second.RunId!, Done)).Status);
    }

    [Fact]
    public async Task A_chat_message_starts_one_run_even_when_the_client_retries()
    {
        ScriptedLlmProviderRegistry.Current = Script();
        var id = await CreateAsync(new PipelineDefinition { Stages = [Stage("answer")] });

        await Workspace(id).PostUserMessage("what's new in solar?", null, "client-1", "user-1");
        await Workspace(id).PostUserMessage("what's new in solar?", null, "client-1", "user-1");

        var s = await WaitForAsync(id, s => s.Runs.Any(r => r.Status == PipelineRunStatus.Completed));
        await Task.Delay(1000);
        s = (await Workspace(id).GetSnapshot())!;
        var run = Assert.Single(s.Runs);
        Assert.Equal("chat", run.Source);
        Assert.Equal("user-1", run.StartedBy);
        Assert.Single(s.Conversation, c => c.AuthorKind == ChatAuthorKind.User);
    }

    [Fact]
    public async Task Edits_make_new_versions_stale_edits_are_refused_and_an_earlier_version_can_be_restored()
    {
        ScriptedLlmProviderRegistry.Current = Script();
        var id = await CreateAsync(new PipelineDefinition { Stages = [Stage("research"), Stage("write", "research")] });
        Assert.Equal(1, (await Workspace(id).GetPipeline())!.Version);

        var added = await Workspace(id).ApplyPipelineEdits(
            [new PipelineEditOp { Op = PipelineEditOps.AddStage, After = "research", Stage = new PipelineStagePatch { Name = "Fact check", Instructions = "Check the facts." } }],
            baseVersion: 1, "user-1", null);
        Assert.True(added.Success, string.Join("; ", added.Errors));
        Assert.Equal(2, added.Pipeline!.Version);
        Assert.Equal(["fact-check"], added.Pipeline.Find("write")!.Inputs);

        var stale = await Workspace(id).ApplyPipelineEdits([new PipelineEditOp { Op = PipelineEditOps.RemoveStage, StageId = "write" }], baseVersion: 1, "user-2", null);
        Assert.True(stale.Conflict);
        Assert.Equal(2, (await Workspace(id).GetPipeline())!.Version);

        var invalid = await Workspace(id).ApplyPipelineEdits([new PipelineEditOp { Op = PipelineEditOps.Connect, From = "write", To = "research" }], baseVersion: 2, "user-1", null);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Errors, e => e.Contains("loop"));

        var restored = await Workspace(id).RestorePipelineVersion(1, "user-1");
        Assert.True(restored.Success);
        Assert.Equal(3, restored.Pipeline!.Version);
        Assert.Null(restored.Pipeline.Find("fact-check"));

        // A run uses the version current when it starts.
        var run = await WaitForRunAsync((await Workspace(id).StartRun("x", "user-1")).RunId!, Done);
        Assert.Equal(3, run.PipelineVersion);
        Assert.Equal(["research", "write"], run.Stages.Select(s => s.StageId));
    }
}
