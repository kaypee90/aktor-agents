using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tasks;
using AgentRuntime.Integrations;
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
    /// <summary>The preview this task was started from; its estimate is kept for estimate vs actual.</summary>
    public string? PreviewId { get; init; }
    /// <summary>Replay a past run from its step journal instead of starting fresh (roadmap P6).</summary>
    public Durability.ReplaySpec? Replay { get; init; }
}

/// <summary>A task (or a request to a workspace) as every protocol reports it.</summary>
public sealed record TaskView
{
    public required string TaskId { get; init; }
    /// <summary>"task", or "workspace_request" for a goal handed to a workspace.</summary>
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
    /// <summary>The aggregated TaskResult (tasks), when ready.</summary>
    public JsonElement? Result { get; init; }
    /// <summary>Workspace requests: what the workspace's agents told the user since the request.</summary>
    public List<string> Replies { get; init; } = [];
}

public sealed record TaskAgentView(string AgentId, string Role, string Status, string? ParentAgentId, int Depth, string Goal);

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
    ILogger<TaskService> logger)
{
    /// <summary>Separates a workspace id from the chat sequence number in a workspace request's id.</summary>
    private const char RequestSeparator = ':';

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

        var taskId = Guid.NewGuid().ToString("n");
        var budget = ceiling.Value.Clamp(request.Budget ?? defaultBudget.Value.ToBudget());

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
                EstimateJson = estimateJson
            });
            await db.SaveChangesAsync(ct);
        }

        if (request.CallbackUrl is not null && !string.IsNullOrEmpty(request.CallbackSecret))
        {
            await secrets.PutAsync(TaskCallbackDispatcher.SecretScope(taskId), TaskCallbackDispatcher.SecretKey, request.CallbackSecret, ct);
        }

        string rootAgentId;
        try
        {
            rootAgentId = await orchestrator.CreateRootAgentAsync(taskId, request.Goal, new TaskLaunchOptions
            {
                Budget = budget,
                TenantId = tenantId,
                CorrelationId = correlationId,
                TeamPolicy = request.TeamPolicy,
                Replay = request.Replay
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
    public async Task<TaskView> ReplayAsync(string tenantId, string sourceTaskId, Durability.ReplayMode mode, long? forkAfterStep,
        IReadOnlyList<Durability.JournalStep> sourceSteps, CancellationToken ct = default)
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
            Replay = new Durability.ReplaySpec { SourceTaskId = sourceTaskId, Mode = mode, ForkAfterSeq = forkAfterStep }
        }, ct);
    }

    /// <summary>Hands the goal to a workspace's coordinator. The handle is "workspace:chat-seq":
    /// the request is done when everything it set off has settled.</summary>
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
            throw new TaskServiceException("callback_url isn't supported for workspace requests; poll get_task_status instead.");
        }

        ChatEntry entry;
        try
        {
            entry = await workspace.PostUserMessage(request.Goal, null, $"{request.Source}-{correlationId}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new TaskServiceException(ex.Message, StatusCodes.Status409Conflict);
        }

        var id = $"{workspaceId}{RequestSeparator}{entry.Seq}";
        logger.LogInformation("Goal handed to workspace {WorkspaceId} via {Source} (request {RequestId}, correlation {CorrelationId})",
            workspaceId, request.Source, id, correlationId);
        return await GetAsync(tenantId, id) ?? throw new TaskServiceException("The workspace request disappeared.", 500);
    }

    /// <summary>The task as the caller's organization sees it, or null (also for another organization's).</summary>
    public async Task<TaskView?> GetAsync(string tenantId, string taskId, CancellationToken ct = default)
    {
        if (TryParseWorkspaceRequest(taskId, out var workspaceId, out var seq))
        {
            return (await GetWorkspaceRequestAsync(tenantId, taskId, workspaceId, seq)).View;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId && t.TenantId == tenantId, ct);
        if (task is null) return null;

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
        if (TryParseWorkspaceRequest(taskId, out var workspaceId, out var seq))
        {
            var (view, replies) = await GetWorkspaceRequestAsync(tenantId, taskId, workspaceId, seq);
            return view is null ? null : new TaskResultView { Task = view, Ready = view.Done, Replies = replies };
        }

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

        if (task.WorkspaceId is { } workspaceId)
        {
            var coordinator = WorkspaceIds.CoordinatorId(workspaceId);
            var live = await orchestrator.FindAgentsAsync(new FindAgentsQuery { RootAgentId = coordinator, TenantId = tenantId }, ct);
            return live.Select(a => new TaskAgentView(a.AgentId, a.Role, a.Status.ToString(), a.ParentAgentId, a.Depth, a.Goal)).ToList();
        }

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

    /// <summary>Stops every agent of the task. Workspace requests can't be cancelled one by one:
    /// pause the workspace instead.</summary>
    public async Task<TaskView?> CancelAsync(string tenantId, string taskId, CancellationToken ct = default)
    {
        var task = await GetAsync(tenantId, taskId, ct);
        if (task is null) return null;
        if (task.Kind == "workspace_request")
        {
            throw new TaskServiceException("A request to a workspace can't be cancelled on its own; pause the workspace instead.", StatusCodes.Status409Conflict);
        }

        await ForEachAgentAsync(tenantId, taskId, orchestrator.StopAsync, ct);
        return await GetAsync(tenantId, taskId, ct);
    }

    public async Task<bool> PauseAsync(string tenantId, string taskId, bool pause, CancellationToken ct = default)
    {
        if (await GetAsync(tenantId, taskId, ct) is not { Kind: "task" }) return false;
        await ForEachAgentAsync(tenantId, taskId, pause ? orchestrator.PauseAsync : orchestrator.ResumeAsync, ct);
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
                var signalled = current.Kind == "task" ? completions.WhenSignalled(taskId) : Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
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
        var scope = task.WorkspaceId ?? task.TaskId;
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

    private async Task<(TaskView? View, List<string> Replies)> GetWorkspaceRequestAsync(string tenantId, string id, string workspaceId, long seq)
    {
        var workspace = grains.GetGrain<IWorkspaceGrain>(workspaceId);
        if (!TenantIds.Same(await workspace.GetTenantId() ?? "\0", tenantId)) return (null, []);
        var snapshot = await workspace.GetSnapshot();
        if (snapshot is null) return (null, []);

        var request = snapshot.Conversation.FirstOrDefault(c => c.Seq == seq && c.AuthorKind == ChatAuthorKind.User);
        if (request is null) return (null, []);

        var replies = snapshot.Conversation.Where(c => c.Seq > seq && c.AuthorKind == ChatAuthorKind.Agent).ToList();
        var agents = await orchestrator.FindAgentsAsync(new FindAgentsQuery { RootAgentId = WorkspaceIds.CoordinatorId(workspaceId), TenantId = tenantId });
        // Settled: nobody is mid-step, and every one-shot worker has finished. Standing agents
        // waiting for their next event don't hold a request open.
        var busy = agents.Count(a => a.Status is AgentStatus.Created or AgentStatus.Initializing or AgentStatus.Thinking or AgentStatus.Executing or AgentStatus.Spawning);
        var snapshotsByAgent = snapshot.Agents.ToDictionary(a => a.AgentId);
        var workersOpen = agents.Count(a => !IsTerminal(a.Status.ToString()) && snapshotsByAgent.TryGetValue(a.AgentId, out var view) && !view.Standing);
        var state = snapshot.Status == WorkspaceStatus.Archived ? "canceled"
            : replies.Count > 0 && busy == 0 && workersOpen == 0 ? "completed"
            : "working";

        var view = new TaskView
        {
            TaskId = id,
            Kind = "workspace_request",
            Goal = request.Text,
            State = state,
            Status = snapshot.Status.ToString(),
            RootAgentId = WorkspaceIds.CoordinatorId(workspaceId),
            WorkspaceId = workspaceId,
            CreatedAt = request.At,
            CompletedAt = state == "completed" ? replies[^1].At : null,
            DashboardUrl = links.Value.WorkspaceUrl(workspaceId),
            AgentsTotal = agents.Count,
            AgentsActive = busy,
            Summary = replies.Count > 0 ? replies[^1].Text : null
        };
        return (view, replies.Select(r => $"{r.AuthorName}: {r.Text}").ToList());
    }

    private static bool TryParseWorkspaceRequest(string id, out string workspaceId, out long seq)
    {
        workspaceId = string.Empty;
        seq = 0;
        var i = id.LastIndexOf(RequestSeparator);
        if (i <= 0 || !WorkspaceIds.IsWorkspace(id) || !long.TryParse(id.AsSpan(i + 1), out seq)) return false;
        workspaceId = id[..i];
        return true;
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
