using AgentRuntime.Contracts;
using AgentRuntime.Pipelines;

namespace AgentRuntime.Workspaces;

/// <summary>The workspace's pipeline (versioned) and its runs: started by a person or a trigger,
/// queued beyond the pipeline's runs-at-once limit, and recorded when they finish.</summary>
public sealed partial class WorkspaceGrain
{
    private const int MaxRunsKept = 200;

    /// <summary>
    /// Workspaces made before pipelines ran on a coordinator agent and standing agents. On first
    /// activation they get a one-stage pipeline doing their goal, their triggers start runs of it,
    /// and the old agents are retired. Their chat, files, connections and settings stay.
    /// </summary>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        if (!Exists || S.Pipeline is not null) return;

        var coordinator = S.CoordinatorAgentId;
        S.CoordinatorAgentId = string.Empty;
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            "This workspace now runs as a pipeline: one stage that does its purpose. Its triggers start runs of it. " +
            "Add, remove or change stages on the Pipeline tab, or describe the change you want.");
        await CommitPipelineAsync(DefaultPipeline(S.Goal), "system", "Converted from the coordinator workspace");

        if (!string.IsNullOrEmpty(coordinator))
        {
            foreach (var agent in await Registry.FindAsync(new FindAgentsQuery { RootAgentId = coordinator }))
            {
                if (!IsTerminal(agent.Status)) await orchestrator.RetireAsync(agent.AgentId, "the workspace became a pipeline");
            }
        }
    }

    public Task<PipelineDefinition?> GetPipeline() => Task.FromResult(Exists ? S.Pipeline : null);

    public Task<List<PipelineDefinition>> GetPipelineHistory() =>
        Task.FromResult(Exists ? S.PipelineHistory.AsEnumerable().Reverse().ToList() : []);

    public async Task<PipelineChangeResult> SetPipeline(PipelineDefinition pipeline, int baseVersion, string changedBy, string note)
    {
        if (!Exists) return new PipelineChangeResult { Errors = ["No such workspace."] };
        if (S.Pipeline is { } current && current.Version != baseVersion) return Conflict(current);

        var errors = PipelineValidator.Validate(pipeline, _pipelineOpts);
        if (errors.Count > 0) return new PipelineChangeResult { Errors = errors };
        var changed = await CommitPipelineAsync(pipeline, changedBy, string.IsNullOrWhiteSpace(note) ? "Edited on the canvas" : note);
        return new PipelineChangeResult { Success = true, Pipeline = changed, Changes = [changed.Note] };
    }

    public async Task<PipelineChangeResult> ApplyPipelineEdits(List<PipelineEditOp> ops, int baseVersion, string changedBy, string? note)
    {
        if (!Exists || S.Pipeline is null) return new PipelineChangeResult { Errors = ["No such workspace."] };
        if (S.Pipeline.Version != baseVersion) return Conflict(S.Pipeline);

        var result = PipelineEditor.Apply(S.Pipeline, ops, _pipelineOpts);
        if (!result.Success) return new PipelineChangeResult { Errors = result.Errors, Changes = result.Changes };
        var changed = await CommitPipelineAsync(result.Pipeline!, changedBy,
            string.IsNullOrWhiteSpace(note) ? string.Join("; ", result.Changes) : note.Trim());
        return new PipelineChangeResult { Success = true, Pipeline = changed, Changes = result.Changes };
    }

    public async Task<PipelineDefinition?> SetPipelineLayout(Dictionary<string, StagePosition> layout)
    {
        if (!Exists || S.Pipeline is null) return null;
        S.Pipeline = S.Pipeline with { Layout = PipelineLayout.Keep(layout, S.Pipeline.Stages) };
        await SaveAsync();
        return S.Pipeline;
    }

    public async Task<PipelineChangeResult> RestorePipelineVersion(int version, string changedBy)
    {
        if (!Exists || S.Pipeline is null) return new PipelineChangeResult { Errors = ["No such workspace."] };
        var earlier = S.PipelineHistory.FirstOrDefault(p => p.Version == version);
        if (earlier is null) return new PipelineChangeResult { Errors = [$"Version {version} isn't kept any more."] };

        var changed = await CommitPipelineAsync(earlier, changedBy, $"Restored version {version}");
        return new PipelineChangeResult { Success = true, Pipeline = changed, Changes = [changed.Note] };
    }

    public Task<RunStartResult> StartRun(string input, string startedBy) =>
        StartRunCoreAsync(input, "manual", startedBy, trigger: null, eventId: null);

    public async Task OnRunFinished(string runId, PipelineRunStatus status, string summary)
    {
        if (!Exists) return;
        var index = S.Runs.FindIndex(r => r.RunId == runId);
        var number = index >= 0 ? S.Runs[index].Number : 0;
        if (index >= 0)
        {
            if (S.Runs[index].CompletedAt is not null) return; // Already recorded.
            S.Runs[index] = S.Runs[index] with { Status = status, CompletedAt = DateTimeOffset.UtcNow, Summary = Clip(summary, 2000, string.Empty) };
        }

        S.ActiveRunIds.Remove(runId);
        var (text, urgency) = status switch
        {
            PipelineRunStatus.Completed => ($"Run #{number} finished.\n\n{summary}", S.Pipeline?.ResultUrgency ?? "info"),
            PipelineRunStatus.Cancelled => ($"Run #{number} was cancelled.", "info"),
            PipelineRunStatus.TimedOut => ($"Run #{number} hit its time limit. {summary}", "warning"),
            _ => ($"Run #{number} failed. {summary}", S.Pipeline?.ResultUrgency == "urgent" ? "urgent" : "warning")
        };
        var chat = AppendChat(ChatAuthorKind.System, runId, $"Run #{number}", Clip(text, _opts.MaxMessageLength, text), urgency);
        QueueNotifications(chat);
        await SaveAsync();
        await KickOutboxAsync();
        await PublishAsync(RuntimeEventType.WorkspaceMessage, $"Run #{number}: {status.ToString().ToLowerInvariant()}", ChatData(chat));
        await DrainRunQueueAsync();
        await ChangedAsync($"run #{number} {status.ToString().ToLowerInvariant()}");
    }

    // ---- Internals ------------------------------------------------------------------

    private async Task<PipelineDefinition> CommitPipelineAsync(PipelineDefinition pipeline, string changedBy, string note)
    {
        var previous = S.Pipeline;
        var next = pipeline with
        {
            Layout = PipelineLayout.Keep(pipeline.Layout, pipeline.Stages),
            Version = (previous?.Version ?? 0) + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = changedBy,
            Note = Clip(note, 300, "Changed")
        };
        if (previous is not null)
        {
            S.PipelineHistory.Add(previous);
            if (S.PipelineHistory.Count > _pipelineOpts.MaxVersionsKept) S.PipelineHistory.RemoveAt(0);
        }

        S.Pipeline = next;
        if (previous is not null)
        {
            AppendChat(ChatAuthorKind.System, "system", "Pipeline", $"Pipeline updated to version {next.Version}: {next.Note}");
        }

        await SaveAsync();
        await AuditAsync("user", changedBy, changedBy, "pipeline.changed", $"v{next.Version}", "ok", next.Note, key: $"{S.WorkspaceId}-pipeline-v{next.Version}");
        await PublishAsync(RuntimeEventType.PipelineChanged, $"Pipeline v{next.Version}: {Truncate(next.Note, 120)}",
            new Dictionary<string, string> { ["version"] = next.Version.ToString(), ["note"] = next.Note });
        await ArchiveAsync();
        return next;
    }

    /// <summary>Starts a run, or queues it when the pipeline's runs-at-once limit is reached. With an
    /// <paramref name="eventId"/> (a trigger tick or webhook delivery) the run id is fixed by it, so
    /// the same event fired twice starts one run.</summary>
    private async Task<RunStartResult> StartRunCoreAsync(string input, string source, string? startedBy, TriggerDefinition? trigger, string? eventId)
    {
        if (!Exists || S.Pipeline is null) return new RunStartResult { Message = "No such workspace." };
        if (S.Status == WorkspaceStatus.Archived) return new RunStartResult { Message = "This workspace is archived." };
        var text = Clip(input, _opts.MaxMessageLength, string.Empty);
        if (text.Length == 0) return new RunStartResult { Message = "A run needs an input: what should the pipeline do this time?" };

        var runId = eventId is null ? PipelineIds.NewRun() : PipelineIds.RunFor(eventId);
        if (S.Runs.FirstOrDefault(r => r.RunId == runId) is { } existing)
        {
            return new RunStartResult { Success = true, RunId = runId, Number = existing.Number, Message = "already started" };
        }

        var request = new PipelineRunRequest
        {
            WorkspaceId = S.WorkspaceId,
            WorkspaceName = S.Name,
            TenantId = Tenancy.TenantIds.Normalize(S.TenantId),
            Pipeline = S.Pipeline,
            Input = text,
            Source = source,
            TriggerId = trigger?.TriggerId,
            TriggerName = trigger?.Name,
            StartedBy = startedBy,
            StageBudget = BuildPolicy().WorkerBudget,
            Team = S.SafetyPolicy.Team,
            Number = S.NextRunNumber
        };

        var waiting = S.Status == WorkspaceStatus.Paused || S.ActiveRunIds.Count >= S.Pipeline.MaxConcurrentRuns;
        if (waiting && S.RunQueue.Count >= _pipelineOpts.MaxQueuedRuns)
        {
            S.DroppedRuns++;
            await SaveAsync();
            return new RunStartResult { Message = $"{S.RunQueue.Count} runs are already waiting; try again when some have finished." };
        }

        S.NextRunNumber++;
        S.Runs.Add(new WorkspaceRunSummary
        {
            RunId = runId,
            Number = request.Number,
            Status = PipelineRunStatus.Queued,
            Source = source,
            TriggerName = trigger?.Name,
            Input = Truncate(text, 300),
            StartedBy = startedBy,
            PipelineVersion = S.Pipeline.Version,
            CreatedAt = DateTimeOffset.UtcNow
        });
        if (S.Runs.Count > MaxRunsKept) S.Runs.RemoveAt(0);

        var label = trigger is null ? "" : $" ({source} '{trigger.Name}')";
        if (waiting)
        {
            // Matched back to its run id (in Runs) by its number when it starts.
            S.RunQueue.Add(request with { Input = text });
            AppendChat(ChatAuthorKind.System, runId, $"Run #{request.Number}",
                $"Run #{request.Number}{label} is queued: {(S.Status == WorkspaceStatus.Paused ? "the workspace is paused" : $"{S.ActiveRunIds.Count} run(s) in progress")}.");
            await SaveAsync();
            await GrainFactory.GetGrain<IPipelineRunGrain>(runId).Queue(request);
            await ChangedAsync($"run #{request.Number} queued");
            return new RunStartResult { Success = true, RunId = runId, Number = request.Number, Message = "queued" };
        }

        await LaunchAsync(runId, request, label);
        return new RunStartResult { Success = true, RunId = runId, Number = request.Number, Message = "started" };
    }

    private async Task LaunchAsync(string runId, PipelineRunRequest request, string label)
    {
        S.ActiveRunIds.Add(runId);
        var index = S.Runs.FindIndex(r => r.RunId == runId);
        if (index >= 0) S.Runs[index] = S.Runs[index] with { Status = PipelineRunStatus.Running };
        var chat = AppendChat(ChatAuthorKind.System, runId, $"Run #{request.Number}",
            $"Run #{request.Number}{label} started: {Truncate(request.Input, 200)}");
        await SaveAsync();
        var byTrigger = request.TriggerId is not null;
        await AuditAsync(byTrigger ? "trigger" : "user", byTrigger ? request.TriggerId : request.StartedBy, byTrigger ? request.TriggerName : request.StartedBy,
            "pipeline.run", runId, "ok", Truncate(request.Input, 300), key: $"{S.WorkspaceId}-run-{runId}");

        await GrainFactory.GetGrain<IPipelineRunGrain>(runId).Start(request);
        await PublishAsync(RuntimeEventType.WorkspaceMessage, $"Run #{request.Number} started.", ChatData(chat));
        await ChangedAsync($"run #{request.Number} started");
    }

    /// <summary>Starts queued runs while there's room.</summary>
    private async Task DrainRunQueueAsync()
    {
        while (S.Status == WorkspaceStatus.Active && S.Pipeline is not null && S.RunQueue.Count > 0 && S.ActiveRunIds.Count < S.Pipeline.MaxConcurrentRuns)
        {
            var next = S.RunQueue[0];
            S.RunQueue.RemoveAt(0);
            var runId = S.Runs.FirstOrDefault(r => r.Number == next.Number)?.RunId ?? PipelineIds.NewRun();
            // Queued runs start on the pipeline as it is now, not as it was when they were queued.
            await LaunchAsync(runId, next with { Pipeline = S.Pipeline, StageBudget = BuildPolicy().WorkerBudget, Team = S.SafetyPolicy.Team }, "");
        }
    }

    /// <summary>How long a finished run stays on the live team view, and how many at most.</summary>
    private static readonly TimeSpan RecentRunWindow = TimeSpan.FromMinutes(30);
    private const int RecentRunsShown = 5;

    /// <summary>Runs in progress and the few that finished lately, each with its agents (the run's
    /// own entry first): what the workspace's live team view draws.</summary>
    private async Task<IReadOnlyList<AgentDirectoryEntry>> RecentRunAgentsAsync()
    {
        var cutoff = DateTimeOffset.UtcNow - RecentRunWindow;
        var runIds = S.Runs.AsEnumerable().Reverse()
            .Where(r => S.ActiveRunIds.Contains(r.RunId) || r.CompletedAt > cutoff)
            .Take(Math.Max(RecentRunsShown, S.ActiveRunIds.Count))
            .Select(r => r.RunId)
            .ToList();
        var all = new List<AgentDirectoryEntry>();
        foreach (var runId in runIds)
        {
            all.AddRange((await Registry.FindAsync(new FindAgentsQuery { RootAgentId = runId })).OrderBy(a => a.Depth));
        }

        return all;
    }

    /// <summary>The pipeline a new workspace starts with when none is given: one stage that does the goal.</summary>
    private static PipelineDefinition DefaultPipeline(string goal) => new()
    {
        Stages =
        [
            new PipelineStage
            {
                StageId = "assistant",
                Name = "Assistant",
                Role = "Assistant",
                Instructions = $"Do what this workspace is for, for each run's input. The workspace's purpose: {goal}",
                Capabilities = ["research", "filesystem"],
                MaxHelpers = 2
            }
        ]
    };

    private static PipelineChangeResult Conflict(PipelineDefinition current) => new()
    {
        Conflict = true,
        Pipeline = current,
        Errors = [$"The pipeline changed (it's at version {current.Version} now). Reload it and try again."]
    };
}
