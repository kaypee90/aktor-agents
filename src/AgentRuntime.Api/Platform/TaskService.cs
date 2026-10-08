using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tasks;
using AgentRuntime.Integrations;
using AgentRuntime.Pipelines;
using AgentRuntime.Tenancy;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

/// <summary>What a caller asks for, whichever protocol it came in on.</summary>
public sealed record StartTaskRequest
{
    public required string Goal { get; init; }
    public ResourceBudget? Budget { get; init; }
    /// <summary>Hand the goal to a workspace's coordinator instead of starting a new task, so it
    /// runs with that workspace's connections, safety policy and daily budget.</summary>
    public string? WorkspaceId { get; init; }
    public string? CallbackUrl { get; init; }
    public string? CallbackSecret { get; init; }
    public string? CorrelationId { get; init; }
    /// <summary>"api", "mcp", "a2a" or "acp".</summary>
    public string Source { get; init; } = "api";
    /// <summary>Team-shape rules for this task, on top of the server's (they can only tighten them).</summary>
    public Safety.TeamPolicy? TeamPolicy { get; init; }
    /// <summary>The caller set the budget's max_children itself; otherwise a fan-out per level sets it.</summary>
    public bool MaxChildrenRequested { get; init; }
    /// <summary>The preview this task was started from; its estimate is kept for estimate vs actual.</summary>
    public string? PreviewId { get; init; }
    /// <summary>Replay a past run from its step journal instead of starting fresh (roadmap P6).</summary>
    public Durability.ReplaySpec? Replay { get; init; }
    /// <summary>The organization model profile to run on (docs/llm-settings.md); null for its default.</summary>
    public string? ModelProfileId { get; init; }
    /// <summary>Files staged with POST /api/uploads to start the task with: they move into its
    /// workspace and the root agent gets them as initial context.</summary>
    public IReadOnlyList<string>? UploadIds { get; init; }
    /// <summary>Who started it (email or user id), recorded on its attachments.</summary>
    public string? By { get; init; }
    /// <summary>Who is starting it, for usage by user in analytics (see <see cref="AgentRuntime.Infrastructure.Identity.Caller.ActorId"/>).</summary>
    public string? StartedBy { get; init; }
    /// <summary>Tool connections (MCP servers, APIs) the task's agents can use from their first step.</summary>
    public IReadOnlyList<Integrations.ConnectionRequest>? Connections { get; init; }
    /// <summary>A study run (docs/studies.md): the task works inside the study's workspace with the
    /// study tools. Set by <see cref="StudyService"/> only.</summary>
    public StudyRunLaunch? Study { get; init; }
}

public sealed record StudyRunLaunch(string StudyId, string WorkspaceId);

/// <summary>A task (or a workspace pipeline's run) as every protocol reports it.</summary>
public sealed record TaskView
{
    public required string TaskId { get; init; }
    /// <summary>"task", or "pipeline_run" for a run of a workspace's pipeline.</summary>
    public string Kind { get; init; } = "task";
    public required string Goal { get; init; }
    /// <summary>working, completed, failed or canceled.</summary>
    public required string State { get; init; }
    public bool Done => State is not "working";
    /// <summary>The runtime's own status (e.g. the root agent's: Thinking, Completed, TimedOut).</summary>
    public string? Status { get; init; }
    public string? RootAgentId { get; init; }
    public string? WorkspaceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? DashboardUrl { get; init; }
    public int AgentsTotal { get; init; }
    public int AgentsActive { get; init; }
    public long TokensUsed { get; init; }
    public decimal CostUsd { get; init; }
    public ResourceBudget? Budget { get; init; }
    public string? Summary { get; init; }
}

public sealed record TaskResultView
{
    public required TaskView Task { get; init; }
    public bool Ready { get; init; }
    /// <summary>The aggregated TaskResult, when ready.</summary>
    public JsonElement? Result { get; init; }
}

public sealed record TaskAgentView(string AgentId, string Role, string Status, string? ParentAgentId, int Depth, string Goal);

/// <summary>A file in a task's chat: one the user attached, or one the agents produced in a round.</summary>
public sealed record TaskChatFile(string ArtifactId, string Path, string FileName, long SizeBytes, string CreatedBy);

/// <summary>One turn of a task's chat: the goal or a follow-up from the user (with any files they
/// attached), or the root agent's report (<c>status</c> completed, partial, failed or terminated)
/// with the files the agents produced for it.</summary>
public sealed record TaskChatEntry(string Id, string Author, string Text, DateTimeOffset At, string? Status = null, string? By = null,
    IReadOnlyList<TaskChatFile>? Files = null, IReadOnlyList<string>? RemainingWork = null);

/// <summary>A file a user uploads to attach to a follow-up.</summary>
public sealed record AttachmentUpload(string FileName, long Length, Func<Stream> Open);

public sealed record TaskProgress(int Progress, int Total, string Message);

public sealed class TaskServiceException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The one way tasks are started, watched and stopped: the REST API, the MCP server, the A2A
/// agent and the ACP endpoint all call this, so budgets, the task budget ceiling, plan quotas and
/// tenant isolation apply identically to every caller. The runtime enforces the limits; this class
/// only makes sure nothing reaches the runtime by another route.
/// </summary>
public sealed class TaskService(
    IAgentOrchestrator orchestrator,
    IDbContextFactory<AgentDbContext> dbFactory,
    IGrainFactory grains,
    TaskCompletionNotifier completions,
    IEventStream events,
    ISecretStore secrets,
    IOptions<TaskLinkOptions> links,
    IOptions<DefaultBudgetOptions> defaultBudget,
    IOptions<TaskBudgetCeilingOptions> ceiling,
    LLM.LlmSettingsService models,
    LLM.ITaskModelSelection taskModels,
    IEventPublisher publisher,
    IOptions<Infrastructure.Tools.ToolsOptions> toolsOptions,
    UploadStore uploads,
    ILogger<TaskService> logger)
{
    /// <summary>Who an attachment is recorded as created by.</summary>
    public const string UserAuthor = "user";


    /// <summary>
    /// With a fan-out per level and no max_children of the caller's own, the budget's child limit
    /// follows the fan-out (its largest entry), so the two can't disagree: the fan-out limits each
    /// level, and each agent's budget is split among the children it may actually start.
    /// </summary>
    private static ResourceBudget WithFanOut(ResourceBudget budget, StartTaskRequest request) =>
        request.TeamPolicy?.MaxFanOutByDepth is { Count: > 0 } fanOut && !request.MaxChildrenRequested
            ? budget with { MaxChildren = Math.Max(0, fanOut.Max()) }
            : budget;

    public static string NewCorrelationId() => "corr-" + Guid.NewGuid().ToString("n")[..16];

    public async Task<TaskView> StartAsync(string tenantId, StartTaskRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Goal)) throw new TaskServiceException("goal is required");
        if (request.Goal.Length > 20_000) throw new TaskServiceException("goal is too long (20,000 characters at most)");

        var correlationId = NormalizeCorrelationId(request.CorrelationId);
        if (request.CallbackUrl is { } url && !TaskCallbackDispatcher.IsAcceptableUrl(url, out var urlError))
        {
            throw new TaskServiceException(urlError!);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            return await StartInWorkspaceAsync(tenantId, request, correlationId);
        }

        var modelProfileId = await CheckModelAsync(tenantId, request.ModelProfileId, ct);
        // Checked before the task exists, so an expired upload doesn't leave a task behind.
        var staged = request.UploadIds is { Count: > 0 } uploadIds ? uploads.Find(tenantId, uploadIds) : [];
        var taskId = request.Study is null ? Guid.NewGuid().ToString("n") : Studies.StudyIds.NewRun();

        // Connected (and their tools discovered) before the task exists, so a server that can't be
        // reached is reported at once and leaves no task behind.
        if (request.Connections is { Count: > 0 } connections)
        {
            var taskConnections = grains.GetGrain<Integrations.ITaskConnectionsGrain>(taskId);
            foreach (var connection in connections)
            {
                var added = await taskConnections.AddConnection(tenantId, connection);
                if (added.Success) continue;
                foreach (var c in await taskConnections.ListConnections()) await taskConnections.RemoveConnection(c.ConnectionId);
                throw new TaskServiceException($"Couldn't connect '{connection.Name}': {added.Message}");
            }
        }
        var budget = ceiling.Value.Clamp(WithFanOut(request.Budget ?? defaultBudget.Value.ToBudget(), request));

        // The row exists before the root agent does, so a caller can poll the id it gets back at once.
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            string? estimateJson = null;
            if (!string.IsNullOrWhiteSpace(request.PreviewId) &&
                await db.TaskPreviews.FirstOrDefaultAsync(p => p.PreviewId == request.PreviewId && p.TenantId == tenantId, ct) is { } preview)
            {
                estimateJson = preview.EstimateJson;
                preview.TaskId ??= taskId;
            }

            db.Tasks.Add(new TaskRecord
            {
                TenantId = tenantId,
                TaskId = taskId,
                Goal = request.Goal,
                Status = "Running",
                CreatedAt = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                Source = request.Source,
                ReplayOfTaskId = request.Replay?.SourceTaskId,
                ReplayMode = request.Replay?.Mode.ToString(),
                ForkAfterStep = request.Replay?.ForkAfterSeq,
                BudgetJson = JsonSerializer.Serialize(budget),
                CallbackUrl = request.CallbackUrl,
                PreviewId = estimateJson is null ? null : request.PreviewId,
                EstimateJson = estimateJson,
                StartedBy = request.StartedBy,
                WorkspaceId = request.Study?.WorkspaceId
            });
            await db.SaveChangesAsync(ct);
        }

        // Before the root agent exists, so its very first step uses the chosen model.
        if (modelProfileId is not null) await taskModels.SetAsync(taskId, modelProfileId, ct);

        if (request.CallbackUrl is not null && !string.IsNullOrEmpty(request.CallbackSecret))
        {
            await secrets.PutAsync(TaskCallbackDispatcher.SecretScope(taskId), TaskCallbackDispatcher.SecretKey, request.CallbackSecret, ct);
        }

        // Files the task starts with: in its workspace before the root agent's first step.
        var agentGoal = request.Goal;
        string? initialContext = null;
        if (staged.Count > 0)
        {
            var files = await StoreAttachmentsAsync(tenantId, taskId,
                staged.Select(s => new AttachmentUpload(s.FileName, s.SizeBytes, () => File.OpenRead(s.FullPath))).ToList(), request.By, "goal", ct);
            foreach (var s in staged) uploads.Release(s);
            agentGoal = $"{request.Goal}\n\nAttached files: {string.Join(", ", files.Select(f => f.Path))}";
            initialContext = await AttachmentContextAsync(taskId, files.ToList(), ct);
        }

        string rootAgentId;
        try
        {
            rootAgentId = await orchestrator.CreateRootAgentAsync(taskId, agentGoal, new TaskLaunchOptions
            {
                Budget = budget,
                TenantId = tenantId,
                CorrelationId = correlationId,
                TeamPolicy = request.TeamPolicy,
                Replay = request.Replay,
                InitialContext = initialContext,
                WorkspaceId = request.Study?.WorkspaceId,
                ExtraTools = request.Study is null ? [] : [.. Tools.AgentToolCatalog.StudyTools]
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            // A plan or runtime limit refused the task: record why, so the id still means something.
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            if (await db.Tasks.FindAsync([taskId], CancellationToken.None) is { } row)
            {
                row.Status = "Rejected";
                row.CompletedAt = DateTimeOffset.UtcNow;
                row.ResultSummary = ex.Message;
                await db.SaveChangesAsync(CancellationToken.None);
            }

            throw new TaskServiceException(ex.Message, StatusCodes.Status409Conflict);
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Tasks.FindAsync([taskId], ct) is { } row && row.RootAgentId is null)
            {
                row.RootAgentId = rootAgentId;
                await db.SaveChangesAsync(ct);
            }
        }

        logger.LogInformation("Task {TaskId} started via {Source} (correlation {CorrelationId}, root {RootAgentId})",
            taskId, request.Source, correlationId, rootAgentId);
        return await GetAsync(tenantId, taskId, ct) ?? throw new TaskServiceException("The task disappeared right after it was created.", 500);
    }

    /// <summary>
    /// Replays a finished task from its step journal (roadmap P6): a new task with the same goal and
    /// budget whose agents take their decisions, and their external tools' results, from the
    /// original. <see cref="Durability.ReplayMode.Full"/> calls no model and nothing outside;
    /// <see cref="Durability.ReplayMode.Fork"/> does the same up to <paramref name="forkAfterStep"/>
    /// (a step's sequence number in the journal) and runs live from there.
    /// </summary>
    /// <param name="modelProfileId">For a fork, the model the live part runs on: the original's when null.</param>
    public async Task<TaskView> ReplayAsync(string tenantId, string sourceTaskId, Durability.ReplayMode mode, long? forkAfterStep,
        IReadOnlyList<Durability.JournalStep> sourceSteps, CancellationToken ct = default, string? modelProfileId = null, string? startedBy = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == sourceTaskId && t.TenantId == tenantId, ct)
                     ?? throw new TaskServiceException($"No task '{sourceTaskId}'.", StatusCodes.Status404NotFound);
        if (sourceSteps.Count == 0) throw new TaskServiceException("This task has no recorded steps to replay.", StatusCodes.Status409Conflict);
        if (mode == Durability.ReplayMode.Fork && forkAfterStep is null) throw new TaskServiceException("A fork needs fork_after_step.");

        return await StartAsync(tenantId, new StartTaskRequest
        {
            Goal = source.Goal,
            Budget = source.BudgetJson is null ? null : JsonSerializer.Deserialize<ResourceBudget>(source.BudgetJson),
            Source = "replay",
            CorrelationId = Clip($"replay-of-{source.CorrelationId ?? sourceTaskId}", 128),
            Replay = new Durability.ReplaySpec { SourceTaskId = sourceTaskId, Mode = mode, ForkAfterSeq = forkAfterStep },
            ModelProfileId = modelProfileId ?? source.ModelProfileId,
            StartedBy = startedBy
        }, ct);
    }

    /// <summary>
    /// Moves a running task to another model profile (docs/llm-settings.md). Every agent of the task,
    /// including ones spawned later, uses it from its next step; finished work is kept. Returns the
    /// profile now in force.
    /// </summary>
    public async Task<string> SwitchModelAsync(string tenantId, string taskId, string profileId, string? by, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId && t.TenantId == tenantId, ct)
                   ?? throw new TaskServiceException($"No task '{taskId}'.", StatusCodes.Status404NotFound);
        if (task.CompletedAt is not null) throw new TaskServiceException("The task has finished; fork it to continue on another model.", StatusCodes.Status409Conflict);

        var next = await CheckModelAsync(tenantId, profileId, ct) ?? LLM.ModelProfiles.ServerId;
        var before = await models.ResolveAsync(tenantId, task.ModelProfileId, ct);
        await taskModels.SetAsync(taskId, next, ct);
        var after = await models.ResolveAsync(tenantId, next, ct);

        await publisher.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.TaskModelChanged,
            TaskId = taskId,
            TenantId = tenantId,
            AgentId = task.RootAgentId,
            Summary = $"Model switched from {ModelLabel(before)} to {ModelLabel(after)}{(by is null ? "" : $" by {by}")}. Agents use it from their next step.",
            Data = new Dictionary<string, string>
            {
                ["from_profile_id"] = before.ProfileId ?? LLM.ModelProfiles.ServerId,
                ["from_model"] = before.Model,
                ["to_profile_id"] = after.ProfileId ?? LLM.ModelProfiles.ServerId,
                ["to_model"] = after.Model,
                ["to_provider"] = after.Provider
            }
        }, ct);
        logger.LogInformation("Task {TaskId} switched to model profile {Profile}", taskId, next);
        return next;
    }

    private static string ModelLabel(Configuration.LlmOptions o) =>
        o.Provider == "Mock" ? "the demo model" : $"{o.ProfileName ?? "the server default"} ({o.Model})";

    /// <summary>A profile the organization has (or "server"); null when none was named.</summary>
    private async Task<string?> CheckModelAsync(string tenantId, string? profileId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        var id = profileId.Trim();
        if (id == LLM.ModelProfiles.ServerId) return id;
        var org = await models.GetAsync(tenantId, ct);
        return org.Profiles.Any(p => p.Id == id) ? id
            : throw new TaskServiceException($"No model '{id}'. Use one set up under Settings → AI model, or \"{LLM.ModelProfiles.ServerId}\".");
    }

    /// <summary>
    /// A follow-up instruction on a task (task chat). A running task's root agent folds it into its
    /// work; a finished one is reopened with its whole history (transcript, earlier results and the
    /// task's files) and a new round of the task's budget, so nothing it learned is lost.
    /// </summary>
    public async Task<TaskChatEntry> FollowUpAsync(string tenantId, string taskId, string text, string? by,
        ResourceBudget? roundBudget = null, CancellationToken ct = default, IReadOnlyList<string>? attachmentIds = null)
    {
        attachmentIds = attachmentIds?.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList() ?? [];
        if (string.IsNullOrWhiteSpace(text) && attachmentIds.Count > 0) text = "Take the attached files into account.";
        if (string.IsNullOrWhiteSpace(text)) throw new TaskServiceException("text is required");
        if (text.Length > 20_000) throw new TaskServiceException("text is too long (20,000 characters at most)");
        if (PipelineIds.IsRun(taskId))
        {
            throw new TaskServiceException("This is a pipeline run; start a new run with the new input instead.", StatusCodes.Status409Conflict);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId && t.TenantId == tenantId, ct)
                   ?? throw new TaskServiceException($"No task '{taskId}'.", StatusCodes.Status404NotFound);
        if (task.RootAgentId is null || task.Status == "Rejected")
        {
            throw new TaskServiceException("This task never started, so there is nothing to follow up on. Start a new task.", StatusCodes.Status409Conflict);
        }

        // Each round gets the task's own budget again (or what the caller asks for), within the ceiling.
        var round = ceiling.Value.Clamp(roundBudget
                                        ?? (task.BudgetJson is null ? null : JsonSerializer.Deserialize<ResourceBudget>(task.BudgetJson))
                                        ?? defaultBudget.Value.ToBudget());
        var trimmed = text.Trim();
        var files = await AttachedFilesAsync(db, tenantId, taskId, attachmentIds, ct);
        var followUp = new TaskFollowUp
        {
            Id = $"followup-{Guid.NewGuid():n}",
            // The file list stays in the agent's standing instructions; the excerpts only in its history.
            Text = files.Count == 0 ? trimmed : $"{trimmed}\n\nAttached files: {string.Join(", ", files.Select(f => f.Path))}",
            AttachmentContext = files.Count == 0 ? null : await AttachmentContextAsync(taskId, files, ct),
            RoundBudget = round,
            By = by
        };

        try
        {
            await orchestrator.FollowUpAsync(task.RootAgentId, followUp, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new TaskServiceException(ex.Message, StatusCodes.Status409Conflict);
        }

        var at = DateTimeOffset.UtcNow;
        await publisher.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.TaskFollowUp,
            TaskId = taskId,
            TenantId = tenantId,
            AgentId = task.RootAgentId,
            CorrelationId = task.CorrelationId,
            Timestamp = at,
            Summary = $"Follow-up from {by ?? "the user"}: {Clip(trimmed, 200)}",
            Data = new Dictionary<string, string>
            {
                ["follow_up_id"] = followUp.Id,
                ["text"] = trimmed,
                ["by"] = by ?? string.Empty,
                ["attachments"] = JsonSerializer.Serialize(files.Select(f => f.ArtifactId))
            }
        }, ct);
        logger.LogInformation("Follow-up {FollowUpId} on task {TaskId} (root {RootAgentId}, {Files} attachment(s))", followUp.Id, taskId, task.RootAgentId, files.Count);
        return new TaskChatEntry(followUp.Id, "user", trimmed, at, By: by, Files: files);
    }

    /// <summary>
    /// Picks a finished task up where it stopped (a partial result, or one cut short by its budget
    /// or time): a follow-up telling the root agent what was left, with <paramref name="roundBudget"/>
    /// for the work, on top of what was already spent. The budget is capped by the server's ceiling
    /// like any other; the agent never sets it.
    /// </summary>
    public async Task<TaskChatEntry> ContinueAsync(string tenantId, string taskId, ResourceBudget? roundBudget, string? note, string? by,
        CancellationToken ct = default)
    {
        TaskRecord? task;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId && t.TenantId == tenantId, ct);
        }

        if (task is null) throw new TaskServiceException($"No task '{taskId}'.", StatusCodes.Status404NotFound);
        // The root agent is the truth: the task row lags a moment behind a reopen.
        var root = task.RootAgentId is null ? null : await orchestrator.GetSnapshotAsync(task.RootAgentId, ct);
        if (task.CompletedAt is null || root is { Status: not (AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Terminated or AgentStatus.TimedOut) })
        {
            throw new TaskServiceException("The task is still running. Send it a message instead, or wait for it to stop.", StatusCodes.Status409Conflict);
        }

        var remaining = new List<string>();
        if (task.ResultJson is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("unresolved_items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                remaining.AddRange(items.EnumerateArray().Select(i => i.GetString()).OfType<string>().Where(i => i.Length > 0));
            }
        }

        var text = new System.Text.StringBuilder("Continue where you stopped and finish the task.");
        if (remaining.Count > 0) text.Append("\n\nWhat was left:\n").AppendJoin("\n", remaining.Select(r => $"- {r}"));
        if (!string.IsNullOrWhiteSpace(note)) text.Append("\n\n").Append(note.Trim());
        text.Append("\n\nYou have a new budget for this. Build on the work and files you already have; don't redo them.");
        return await FollowUpAsync(tenantId, taskId, text.ToString(), by, roundBudget, ct);
    }

    /// <summary>
    /// Saves files the user attaches to a task (task chat) under attachments/ in the task's sandboxed
    /// workspace, where its agents can read them, and records each as an artifact so it can be
    /// previewed and referenced by a follow-up. Any file type is accepted; size and count are capped.
    /// </summary>
    public async Task<IReadOnlyList<TaskChatFile>> AddAttachmentsAsync(string tenantId, string taskId, IReadOnlyList<AttachmentUpload> uploads,
        string? by, CancellationToken ct = default)
    {
        var opts = toolsOptions.Value;
        if (uploads.Count == 0) throw new TaskServiceException("Attach at least one file.");
        if (uploads.Count > opts.AttachmentMaxFiles) throw new TaskServiceException($"Attach at most {opts.AttachmentMaxFiles} files at a time.");
        if (uploads.FirstOrDefault(u => u.Length > opts.AttachmentMaxBytes) is { } big)
        {
            throw new TaskServiceException($"'{big.FileName}' is too large ({Infrastructure.Documents.DocumentFormats.HumanSize(big.Length)}); " +
                                           $"files can be at most {Infrastructure.Documents.DocumentFormats.HumanSize(opts.AttachmentMaxBytes)}.",
                StatusCodes.Status413PayloadTooLarge);
        }

        if (PipelineIds.IsRun(taskId))
        {
            throw new TaskServiceException("This is a pipeline run; files go with the run's input or a new run.", StatusCodes.Status409Conflict);
        }

        await using (var check = await dbFactory.CreateDbContextAsync(ct))
        {
            if (!await check.Tasks.AnyAsync(t => t.TaskId == taskId && t.TenantId == tenantId, ct))
            {
                throw new TaskServiceException($"No task '{taskId}'.", StatusCodes.Status404NotFound);
            }
        }

        return await StoreAttachmentsAsync(tenantId, taskId, uploads, by, "follow-up", ct);
    }

    /// <summary>Copies files into the task's attachments/ folder and records them as artifacts
    /// (<paramref name="attachedTo"/>: "goal" or "follow-up").</summary>
    private async Task<IReadOnlyList<TaskChatFile>> StoreAttachmentsAsync(string tenantId, string taskId, IReadOnlyList<AttachmentUpload> uploads,
        string? by, string attachedTo, CancellationToken ct)
    {
        var opts = toolsOptions.Value;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var root = Infrastructure.Tools.WorkspacePath.TaskRoot(opts, taskId);
        var saved = new List<(Infrastructure.Persistence.ArtifactRecord Record, TaskChatFile File)>();
        foreach (var upload in uploads)
        {
            var fullPath = UniquePath(Infrastructure.Tools.WorkspacePath.Resolve(opts, taskId, $"attachments/{SafeFileName(upload.FileName)}"));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using (var target = File.Create(fullPath))
            await using (var source = upload.Open())
            {
                await source.CopyToAsync(target, ct);
            }

            var kind = Infrastructure.Documents.DocumentFormats.KindOf(fullPath);
            var record = new Infrastructure.Persistence.ArtifactRecord
            {
                TenantId = tenantId,
                ArtifactId = Guid.NewGuid().ToString("n"),
                Type = (kind switch
                {
                    Infrastructure.Documents.DocumentKind.Image => ArtifactType.Image,
                    Infrastructure.Documents.DocumentKind.Excel or Infrastructure.Documents.DocumentKind.Csv => ArtifactType.Data,
                    _ => ArtifactType.Document
                }).ToString(),
                Location = fullPath,
                CreatedByAgent = UserAuthor,
                TaskId = taskId,
                CreatedAt = DateTimeOffset.UtcNow,
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["source"] = "attachment",
                    ["attached_to"] = attachedTo,
                    ["original_name"] = upload.FileName,
                    ["uploaded_by"] = by ?? string.Empty
                })
            };
            db.Artifacts.Add(record);
            var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            saved.Add((record, new TaskChatFile(record.ArtifactId, relative, Path.GetFileName(fullPath), upload.Length, UserAuthor)));
        }

        // Saved before answering, so a follow-up can reference them straight away.
        await db.SaveChangesAsync(ct);
        foreach (var (record, file) in saved)
        {
            await publisher.PublishAsync(new RuntimeEvent
            {
                Type = RuntimeEventType.ArtifactCreated,
                TaskId = taskId,
                TenantId = tenantId,
                AgentId = UserAuthor,
                Summary = $"{by ?? "The user"} attached '{file.Path}' ({Infrastructure.Documents.DocumentFormats.HumanSize(file.SizeBytes)}).",
                Data = new Dictionary<string, string>
                {
                    ["artifactId"] = record.ArtifactId,
                    ["type"] = record.Type,
                    ["location"] = record.Location,
                    ["source"] = "attachment"
                }
            }, ct);
        }

        logger.LogInformation("{Count} attachment(s) added to task {TaskId}", saved.Count, taskId);
        return saved.Select(s => s.File).ToList();
    }

    private async Task<List<TaskChatFile>> AttachedFilesAsync(AgentDbContext db, string tenantId, string taskId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var opts = toolsOptions.Value;
        var root = Infrastructure.Tools.WorkspacePath.TaskRoot(opts, taskId);
        var records = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == taskId && a.TenantId == tenantId && ids.Contains(a.ArtifactId))
            .ToListAsync(ct);
        var files = records
            .Where(r => Infrastructure.Tools.WorkspacePath.IsInsideTaskRoot(opts, taskId, r.Location) && File.Exists(r.Location))
            .OrderBy(r => ids.ToList().IndexOf(r.ArtifactId))
            .Select(r => new TaskChatFile(r.ArtifactId, Path.GetRelativePath(root, r.Location).Replace(Path.DirectorySeparatorChar, '/'),
                Path.GetFileName(r.Location), new FileInfo(r.Location).Length, r.CreatedByAgent))
            .ToList();
        if (files.Count != ids.Count) throw new TaskServiceException("One or more attached files don't exist on this task. Upload them again.");
        return files;
    }

    /// <summary>What the agent sees of the attached files: each one's path, type and size, and the
    /// start of its text, within an overall budget. The full text is a filesystem_read away.</summary>
    private async Task<string> AttachmentContextAsync(string taskId, List<TaskChatFile> files, CancellationToken ct)
    {
        const int total = 16_000, perFile = 6_000;
        var opts = toolsOptions.Value;
        var left = total;
        var sb = new System.Text.StringBuilder("[Attached files] (in your task workspace; read the full text with filesystem_read)\n");
        foreach (var file in files)
        {
            var fullPath = Infrastructure.Tools.WorkspacePath.Resolve(opts, taskId, file.Path);
            var kind = Infrastructure.Documents.DocumentFormats.KindOf(fullPath);
            sb.Append($"\n--- {file.Path} ({kind}, {Infrastructure.Documents.DocumentFormats.HumanSize(file.SizeBytes)}) ---\n");
            var content = await Infrastructure.Documents.DocumentReader.ReadAsync(fullPath, new Infrastructure.Documents.ReadLimits(MaxChars: Math.Max(0, Math.Min(perFile, left))), ct);
            if (content.Note is not null) sb.Append($"({content.Note})\n");
            if (content.Text.Length > 0)
            {
                sb.Append(content.Text);
                if (content.Truncated) sb.Append("\n… (excerpt; read the file for the rest)");
                sb.Append('\n');
                left -= content.Text.Length;
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string SafeFileName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        file = new string(file.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimStart('.');
        if (file.Length == 0) file = "file";
        if (file.Length > 120)
        {
            var ext = Path.GetExtension(file);
            file = file[..(120 - Math.Min(ext.Length, 20))] + (ext.Length <= 20 ? ext : string.Empty);
        }

        return file;
    }

    /// <summary>report.pdf, then report (2).pdf, ... so an upload never replaces an earlier file.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>The task's conversation: its goal, every follow-up, and each report the root agent
    /// gave (one per round), oldest first. Rebuilt from the persisted event history.</summary>
    public async Task<IReadOnlyList<TaskChatEntry>?> GetChatAsync(string tenantId, string taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId && t.TenantId == tenantId, ct);
        if (task is null) return null;

        var root = task.RootAgentId;
        string[] reports = [nameof(RuntimeEventType.AgentCompleted), nameof(RuntimeEventType.AgentFailed), nameof(RuntimeEventType.AgentTerminated)];
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.TaskId == taskId && e.TenantId == tenantId &&
                        (e.Type == nameof(RuntimeEventType.TaskFollowUp) || (e.AgentId == root && reports.Contains(e.Type))))
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        // Files: what the user attached to each follow-up, and what the agents wrote in each round
        // (between the previous message from the user and the root's report).
        var opts = toolsOptions.Value;
        var workspaceRoot = Infrastructure.Tools.WorkspacePath.TaskRoot(opts, taskId);
        var artifacts = (await db.Artifacts.AsNoTracking().Where(a => a.TaskId == taskId && a.TenantId == tenantId).ToListAsync(ct))
            .Where(a => Infrastructure.Tools.WorkspacePath.IsInsideTaskRoot(opts, taskId, a.Location) && File.Exists(a.Location))
            .ToList();
        TaskChatFile FileOf(Infrastructure.Persistence.ArtifactRecord a) => new(a.ArtifactId,
            Path.GetRelativePath(workspaceRoot, a.Location).Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetFileName(a.Location), new FileInfo(a.Location).Length, a.CreatedByAgent);

        var goalFiles = artifacts.Where(a => a.CreatedByAgent == UserAuthor && a.MetadataJson.Contains("\"attached_to\":\"goal\""))
            .OrderBy(a => a.CreatedAt).Select(FileOf).ToList();
        var chat = new List<TaskChatEntry> { new("goal", "user", task.Goal, task.CreatedAt, Files: goalFiles.Count == 0 ? null : goalFiles) };
        var roundStart = task.CreatedAt;
        foreach (var e in rows)
        {
            Dictionary<string, string> data;
            try { data = JsonSerializer.Deserialize<Dictionary<string, string>>(e.DataJson) ?? []; }
            catch (JsonException) { data = []; }

            if (e.Type == nameof(RuntimeEventType.TaskFollowUp))
            {
                List<string> ids;
                try { ids = JsonSerializer.Deserialize<List<string>>(data.GetValueOrDefault("attachments", "[]")) ?? []; }
                catch (JsonException) { ids = []; }
                var attached = ids.Select(id => artifacts.FirstOrDefault(a => a.ArtifactId == id)).OfType<Infrastructure.Persistence.ArtifactRecord>()
                    .Select(FileOf).ToList();
                chat.Add(new TaskChatEntry(data.GetValueOrDefault("follow_up_id", e.EventId), "user",
                    data.GetValueOrDefault("text", e.Summary), e.Timestamp, By: data.GetValueOrDefault("by") is { Length: > 0 } by ? by : null,
                    Files: attached.Count == 0 ? null : attached));
                roundStart = e.Timestamp;
                continue;
            }

            // One entry per file (its latest write), in the order the agents first wrote them.
            var produced = artifacts
                .Where(a => a.CreatedByAgent != UserAuthor && a.CreatedAt >= roundStart && a.CreatedAt <= e.Timestamp)
                .GroupBy(a => a.Location)
                .Select(g => (First: g.Min(a => a.CreatedAt), Latest: g.MaxBy(a => a.CreatedAt)!))
                .OrderBy(x => x.First)
                .Select(x => FileOf(x.Latest))
                .ToList();
            var (text, status) = e.Type switch
            {
                nameof(RuntimeEventType.AgentCompleted) => (data.GetValueOrDefault("summary", e.Summary), data.GetValueOrDefault("status", "completed")),
                nameof(RuntimeEventType.AgentFailed) => (e.Summary, "failed"),
                _ => (e.Summary, "terminated")
            };
            List<string> remaining;
            try { remaining = JsonSerializer.Deserialize<List<string>>(data.GetValueOrDefault("remaining_work", "[]")) ?? []; }
            catch (JsonException) { remaining = []; }
            chat.Add(new TaskChatEntry(e.EventId, "agent", text, e.Timestamp, status, Files: produced.Count == 0 ? null : produced,
                RemainingWork: remaining.Count == 0 ? null : remaining));
            roundStart = e.Timestamp;
        }

        return chat;
    }

    /// <summary>Starts a run of the workspace's pipeline with the goal as its input. The run is a
    /// task (its id is the task id), so callers watch it like any other.</summary>
    private async Task<TaskView> StartInWorkspaceAsync(string tenantId, StartTaskRequest request, string correlationId)
    {
        var workspaceId = request.WorkspaceId!;
        var workspace = grains.GetGrain<IWorkspaceGrain>(workspaceId);
        if (!WorkspaceIds.IsWorkspace(workspaceId) || !TenantIds.Same(await workspace.GetTenantId() ?? "\0", tenantId))
        {
            throw new TaskServiceException($"No such workspace '{workspaceId}'.", StatusCodes.Status404NotFound);
        }

        if (request.CallbackUrl is not null)
        {
            throw new TaskServiceException("callback_url isn't supported for workspace runs; poll get_task_status instead.");
        }

        var started = await workspace.StartRun(request.Goal, request.StartedBy ?? request.Source);
        if (!started.Success || started.RunId is null) throw new TaskServiceException(started.Message, StatusCodes.Status409Conflict);

        logger.LogInformation("Run {RunId} of workspace {WorkspaceId} {Outcome} via {Source} (correlation {CorrelationId})",
            started.RunId, workspaceId, started.Message, request.Source, correlationId);
        return await GetAsync(tenantId, started.RunId) ?? throw new TaskServiceException("The run disappeared.", 500);
    }

    /// <summary>The task as the caller's organization sees it, or null (also for another organization's).</summary>
    public async Task<TaskView?> GetAsync(string tenantId, string taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId && t.TenantId == tenantId, ct);
        if (task is null) return PipelineIds.IsRun(taskId) ? await GetUnsavedRunAsync(tenantId, taskId) : null;

        var agents = await db.Agents.AsNoTracking().Where(a => a.TaskId == taskId)
            .Select(a => new { a.Status, a.TokensUsed, a.CostUsd }).ToListAsync(ct);

        AgentSnapshot? root = null;
        if (task.CompletedAt is null && task.RootAgentId is not null)
        {
            root = await orchestrator.GetSnapshotAsync(task.RootAgentId, ct);
        }

        return new TaskView
        {
            TaskId = task.TaskId,
            Kind = task.Source == "pipeline" ? "pipeline_run" : "task",
            WorkspaceId = task.WorkspaceId,
            Goal = task.Goal,
            State = StateOf(task),
            Status = root?.Status.ToString() ?? task.Status,
            RootAgentId = task.RootAgentId,
            CreatedAt = task.CreatedAt,
            CompletedAt = task.CompletedAt,
            CorrelationId = task.CorrelationId,
            DashboardUrl = links.Value.TaskUrl(task.TaskId),
            AgentsTotal = agents.Count,
            AgentsActive = agents.Count(a => !IsTerminal(a.Status)),
            TokensUsed = agents.Sum(a => (long)a.TokensUsed),
            CostUsd = agents.Sum(a => a.CostUsd),
            Budget = task.BudgetJson is null ? null : JsonSerializer.Deserialize<ResourceBudget>(task.BudgetJson),
            Summary = task.ResultSummary
        };
    }

    public async Task<TaskResultView?> GetResultAsync(string tenantId, string taskId, CancellationToken ct = default)
    {
        var task = await GetAsync(tenantId, taskId, ct);
        if (task is null) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.Tasks.AsNoTracking().Where(t => t.TaskId == taskId).Select(t => t.ResultJson).FirstOrDefaultAsync(ct);
        JsonElement? result = null;
        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            result = doc.RootElement.Clone();
        }

        return new TaskResultView { Task = task, Ready = task.Done && result is not null, Result = result };
    }

    public async Task<IReadOnlyList<TaskAgentView>?> ListAgentsAsync(string tenantId, string taskId, CancellationToken ct = default)
    {
        var task = await GetAsync(tenantId, taskId, ct);
        if (task is null) return null;

        if (task.RootAgentId is { } root)
        {
            var live = await orchestrator.FindAgentsAsync(new FindAgentsQuery { RootAgentId = root, TenantId = tenantId }, ct);
            if (live.Count > 0)
            {
                return live.OrderBy(a => a.Depth).Select(a => new TaskAgentView(a.AgentId, a.Role, a.Status.ToString(), a.ParentAgentId, a.Depth, a.Goal)).ToList();
            }
        }

        // The live registry was reset: the durable history still knows the team.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Agents.AsNoTracking().Where(a => a.TaskId == taskId && a.TenantId == tenantId).OrderBy(a => a.Depth)
            .Select(a => new TaskAgentView(a.AgentId, a.Role, a.Status, a.ParentAgentId, a.Depth, a.Goal)).ToListAsync(ct);
    }

    /// <summary>Stops every agent of the task. A pipeline run is stopped by the run itself, which
    /// stops its agents and skips its unfinished stages.</summary>
    public async Task<TaskView?> CancelAsync(string tenantId, string taskId, CancellationToken ct = default)
    {
        var task = await GetAsync(tenantId, taskId, ct);
        if (task is null) return null;
        if (task.Kind == "pipeline_run") await orchestrator.StopAsync(taskId, ct);
        else await ForEachAgentAsync(tenantId, taskId, orchestrator.StopAsync, ct);
        return await GetAsync(tenantId, taskId, ct);
    }

    public async Task<bool> PauseAsync(string tenantId, string taskId, bool pause, CancellationToken ct = default)
    {
        var task = await GetAsync(tenantId, taskId, ct);
        if (task is null) return false;
        if (task.Kind == "pipeline_run") await (pause ? orchestrator.PauseAsync(taskId, ct) : orchestrator.ResumeAsync(taskId, ct));
        else await ForEachAgentAsync(tenantId, taskId, pause ? orchestrator.PauseAsync : orchestrator.ResumeAsync, ct);
        return true;
    }

    private async Task ForEachAgentAsync(string tenantId, string taskId, Func<string, CancellationToken, Task> action, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Agents.AsNoTracking().Where(a => a.TaskId == taskId && a.TenantId == tenantId).Select(a => a.AgentId).ToListAsync(ct);
        var root = await db.Tasks.AsNoTracking().Where(t => t.TaskId == taskId).Select(t => t.RootAgentId).FirstOrDefaultAsync(ct);
        if (root is not null)
        {
            // Agents created a moment ago may not have reached the history tables yet.
            var live = await orchestrator.FindAgentsAsync(new FindAgentsQuery { RootAgentId = root, TenantId = tenantId }, ct);
            ids = ids.Union(live.Select(a => a.AgentId)).ToList();
        }

        foreach (var id in ids)
        {
            await action(id, ct);
        }
    }

    /// <summary>
    /// Waits until the task is done or <paramref name="timeout"/> passes, reporting the team's
    /// progress (agents finished out of agents started) as it goes. Returns the task as it is then;
    /// <see cref="TaskView.Done"/> says which it was.
    /// </summary>
    public async Task<TaskView?> WaitAsync(string tenantId, string taskId, TimeSpan timeout,
        Func<TaskProgress, ValueTask>? onProgress = null, CancellationToken ct = default)
    {
        var current = await GetAsync(tenantId, taskId, ct);
        if (current is null || current.Done || timeout <= TimeSpan.Zero) return current;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var progressLoop = onProgress is null ? Task.CompletedTask : ReportProgressAsync(tenantId, current, onProgress, deadline.Token);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                // Register before re-reading so a completion in between isn't missed.
                var signalled = completions.WhenSignalled(taskId);
                current = await GetAsync(tenantId, taskId, ct);
                if (current is null || current.Done) return current;

                var poll = Task.Delay(TimeSpan.FromSeconds(5), deadline.Token);
                await Task.WhenAny(signalled, poll);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out: report where it is.
        }
        finally
        {
            await deadline.CancelAsync();
            await progressLoop.ContinueWith(_ => { }, CancellationToken.None);
        }

        return await GetAsync(tenantId, taskId, ct);
    }

    private async Task ReportProgressAsync(string tenantId, TaskView task, Func<TaskProgress, ValueTask> onProgress, CancellationToken ct)
    {
        var scope = task.TaskId;
        var started = Math.Max(1, task.AgentsTotal);
        var finished = task.AgentsTotal - task.AgentsActive;
        await onProgress(new TaskProgress(finished, started, $"{task.AgentsActive} agent(s) working"));

        await foreach (var evt in events.Subscribe(ct))
        {
            if (!TenantIds.Same(evt.TenantId, tenantId) || evt.TaskId != scope) continue;
            string? message = null;
            switch (evt.Type)
            {
                case RuntimeEventType.AgentSpawned:
                    started++;
                    message = evt.Summary;
                    break;
                case RuntimeEventType.AgentCompleted or RuntimeEventType.AgentFailed or RuntimeEventType.AgentTerminated:
                    finished++;
                    message = evt.Summary;
                    break;
                case RuntimeEventType.AgentMessageSent or RuntimeEventType.AgentToolCalled:
                    message = evt.Summary;
                    break;
            }

            if (message is not null)
            {
                await onProgress(new TaskProgress(Math.Min(finished, started), Math.Max(started, finished), message));
            }
        }
    }

    /// <summary>A run whose task row isn't written yet (it's written from the run's first event, a
    /// moment after it starts): reported from the run itself, if it belongs to the organization.</summary>
    private async Task<TaskView?> GetUnsavedRunAsync(string tenantId, string runId)
    {
        var run = await grains.GetGrain<IPipelineRunGrain>(runId).GetView();
        if (run is null || !TenantIds.Same(await grains.GetGrain<IWorkspaceGrain>(run.WorkspaceId).GetTenantId() ?? "\0", tenantId)) return null;
        return new TaskView
        {
            TaskId = runId,
            Kind = "pipeline_run",
            WorkspaceId = run.WorkspaceId,
            Goal = run.Input,
            State = "working",
            Status = run.Status.ToString(),
            RootAgentId = runId,
            CreatedAt = run.CreatedAt,
            DashboardUrl = links.Value.TaskUrl(runId),
            AgentsTotal = run.Stages.Count(st => st.AgentId is not null),
            AgentsActive = run.Stages.Count(st => st.Status == StageRunStatus.Running)
        };
    }

    /// <summary>working until the result is saved; then completed, failed or canceled.</summary>
    private static string StateOf(TaskRecord task)
    {
        if (task.CompletedAt is null) return "working";
        if (task.Status is "Rejected" or "Failed" or "TimedOut") return "failed";
        if (task.Status == "Terminated") return "canceled";
        if (task.ResultJson is not null)
        {
            using var doc = JsonDocument.Parse(task.ResultJson);
            if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "failed") return "failed";
        }

        return "completed";
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    private static bool IsTerminal(string status) => status is "Completed" or "Failed" or "Terminated" or "TimedOut";

    private static string NormalizeCorrelationId(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return NewCorrelationId();
        var trimmed = requested.Trim();
        if (trimmed.Length > 128 || trimmed.Any(c => char.IsControl(c)))
        {
            throw new TaskServiceException("correlation_id must be at most 128 printable characters.");
        }

        return trimmed;
    }
}
