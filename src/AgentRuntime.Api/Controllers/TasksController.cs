using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Api.Platform;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Controllers;

public sealed record CreateTaskRequest(
    string Goal,
    ResourceBudget? Budget,
    string? CallbackUrl = null,
    string? CallbackSecret = null,
    string? CorrelationId = null,
    AgentRuntime.Safety.TeamPolicy? TeamPolicy = null,
    string? PreviewId = null);

public sealed record PreviewTaskRequest(string Goal, ResourceBudget? Budget);

/// <summary>mode: "full" or "fork"; fork_after_step: the journal sequence number the fork runs live after.</summary>
public sealed record ReplayTaskRequest(string Mode = "full", long? ForkAfterStep = null);

[ApiController]
[Route("api/tasks")]
public sealed class TasksController(IAgentOrchestrator orchestrator, AgentDbContext db, IOptions<ToolsOptions> toolsOptions, TenantAccess access,
    TaskService tasks, TaskPreviewService previews, AgentRuntime.Durability.IStepJournal journal) : ControllerBase
{
    /// <summary>Submits a high-level human goal (CLAUDE.md section 1). This is the only manual step —
    /// everything after this is autonomous. The same task service runs MCP, A2A and ACP tasks.</summary>
    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Create([FromBody] CreateTaskRequest request, CancellationToken ct)
    {
        try
        {
            var task = await tasks.StartAsync(access.TenantId, new StartTaskRequest
            {
                Goal = request.Goal,
                Budget = request.Budget,
                CallbackUrl = request.CallbackUrl,
                CallbackSecret = request.CallbackSecret,
                CorrelationId = request.CorrelationId ?? Request.Headers["X-Correlation-Id"].FirstOrDefault(),
                Source = "api",
                TeamPolicy = request.TeamPolicy,
                PreviewId = request.PreviewId
            }, ct);

            return CreatedAtAction(nameof(Get), new { id = task.TaskId }, new
            {
                task_id = task.TaskId,
                root_agent_id = task.RootAgentId,
                correlation_id = task.CorrelationId,
                dashboard_url = task.DashboardUrl,
                budget = task.Budget
            });
        }
        catch (TaskServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    /// <summary>Cost and team preview (roadmap P2): one planning call returns the team the root would
    /// likely build and token, dollar and time ranges. Start the task with its preview_id to have the
    /// estimate compared with what actually happened.</summary>
    [HttpPost("preview")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Preview([FromBody] PreviewTaskRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await previews.PreviewAsync(access.TenantId, request.Goal, request.Budget, ct));
        }
        catch (TaskServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    /// <summary>Spend against budget for each agent of the task and for each branch of its tree (the
    /// agent plus everything below it). Every child's budget is carved from its parent's, so a
    /// branch's spend stays inside the budget of the agent at its top.</summary>
    [HttpGet("{id}/spend")]
    public async Task<IActionResult> Spend(string id, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == id && t.TenantId == access.TenantId, ct);
        if (task?.RootAgentId is null) return NotFound();

        var team = await orchestrator.FindAgentsAsync(new FindAgentsQuery { RootAgentId = task.RootAgentId, TenantId = access.TenantId }, ct);
        var snapshots = (await Task.WhenAll(team.Select(a => orchestrator.GetSnapshotAsync(a.AgentId, ct))))
            .OfType<AgentSnapshot>().ToDictionary(s => s.AgentId);
        var children = snapshots.Values.Where(s => s.ParentAgentId is not null).ToLookup(s => s.ParentAgentId!);

        (long Tokens, decimal Cost) Branch(string agentId)
        {
            var own = snapshots[agentId].Usage;
            long tokens = own.TokensUsed + own.LifetimeTokens;
            var cost = own.CostUsd + own.LifetimeCostUsd;
            foreach (var child in children[agentId])
            {
                var (t, c) = Branch(child.AgentId);
                tokens += t;
                cost += c;
            }

            return (tokens, cost);
        }

        return Ok(snapshots.Values.OrderBy(s => s.Depth).Select(s =>
        {
            var (branchTokens, branchCost) = Branch(s.AgentId);
            return new
            {
                agent_id = s.AgentId,
                parent_agent_id = s.ParentAgentId,
                role = s.Role,
                tokens_used = s.Usage.TokensUsed,
                cost_usd = Math.Round(s.Usage.CostUsd, 6),
                budget_max_tokens = s.Budget.MaxTokens,
                budget_max_cost_usd = s.Budget.MaxCostUsd,
                branch_tokens = branchTokens,
                branch_cost_usd = Math.Round(branchCost, 6)
            };
        }));
    }

    /// <summary>Replays a finished task from its step journal: in full (no model calls, no external
    /// tool calls) or as a fork that runs live after a given step.</summary>
    [HttpPost("{id}/replay")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Replay(string id, [FromBody] ReplayTaskRequest? request, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        if (!Enum.TryParse<AgentRuntime.Durability.ReplayMode>(request?.Mode ?? "full", ignoreCase: true, out var mode))
        {
            return BadRequest(new { error = "mode must be full or fork" });
        }

        try
        {
            var replay = await tasks.ReplayAsync(access.TenantId, id, mode, request?.ForkAfterStep, await journal.ListAsync(id, ct), ct);
            return CreatedAtAction(nameof(Get), new { id = replay.TaskId }, replay);
        }
        catch (TaskServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    /// <summary>Every recorded step of the task, in order: each LLM decision and tool result, by
    /// agent (the dashboard's step-through view).</summary>
    [HttpGet("{id}/journal")]
    public async Task<IActionResult> Journal(string id, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        var roles = await db.Agents.AsNoTracking().Where(a => a.TaskId == id).ToDictionaryAsync(a => a.AgentId, a => a.Role, ct);
        var steps = await journal.ListAsync(id, ct);
        return Ok(steps.Select(s => new
        {
            seq = s.Seq,
            agent_id = s.AgentId,
            agent_path = s.AgentPath,
            role = roles.GetValueOrDefault(s.AgentId),
            kind = s.Kind,
            key = s.Key,
            step = s.Step,
            tool_name = s.ToolName,
            inputs_received = s.InputsReceived,
            at = s.At,
            summary = AgentRuntime.Durability.RunDiff.Summarize(s),
            payload = JsonDocument.Parse(s.PayloadJson).RootElement
        }));
    }

    /// <summary>Two runs compared step by step, aligned by agent position (e.g. a replay against
    /// its original). Run-specific ids are normalized away.</summary>
    [HttpGet("{id}/diff/{otherId}")]
    public async Task<IActionResult> Diff(string id, string otherId, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct) || !await access.TaskAsync(otherId, ct)) return NotFound();
        var diff = AgentRuntime.Durability.RunDiff.Compare(await journal.ListAsync(id, ct), await journal.ListAsync(otherId, ct));
        return Ok(new
        {
            a = id,
            b = otherId,
            identical = diff.Identical,
            same = diff.Same,
            different = diff.Different,
            only_in_a = diff.OnlyInA,
            only_in_b = diff.OnlyInB,
            agents_only_in_a = diff.AgentsOnlyInA,
            agents_only_in_b = diff.AgentsOnlyInB,
            steps = diff.Steps
        });
    }

    /// <summary>Long poll: answers when the task finishes or after <paramref name="timeoutSeconds"/>
    /// (at most 300), whichever comes first. <c>done</c> says which.</summary>
    [HttpGet("{id}/wait")]
    public async Task<IActionResult> Wait(string id, [FromQuery(Name = "timeout_seconds")] int timeoutSeconds = 60, CancellationToken ct = default)
    {
        var task = await tasks.WaitAsync(access.TenantId, id, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, 300)), ct: ct);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == id && t.TenantId == access.TenantId, ct);
        if (task is null) return NotFound();

        AgentSnapshot? rootSnapshot = task.RootAgentId is null
            ? null
            : await orchestrator.GetSnapshotAsync(task.RootAgentId, ct);

        return Ok(new
        {
            task_id = task.TaskId,
            goal = task.Goal,
            status = rootSnapshot?.Status.ToString() ?? task.Status,
            root_agent_id = task.RootAgentId,
            created_at = task.CreatedAt,
            completed_at = task.CompletedAt,
            result_summary = task.ResultSummary,
            correlation_id = task.CorrelationId,
            source = task.Source,
            replay_of_task_id = task.ReplayOfTaskId,
            replay_mode = task.ReplayMode,
            fork_after_step = task.ForkAfterStep
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.TenantId == access.TenantId).OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync(ct);
        return Ok(tasks.Select(t => new { task_id = t.TaskId, goal = t.Goal, status = t.Status, created_at = t.CreatedAt }));
    }

    [HttpPost("{id}/pause")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Pause(string id, CancellationToken ct) =>
        await tasks.PauseAsync(access.TenantId, id, pause: true, ct) ? NoContent() : NotFound();

    [HttpPost("{id}/resume")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Resume(string id, CancellationToken ct) =>
        await tasks.PauseAsync(access.TenantId, id, pause: false, ct) ? NoContent() : NotFound();

    [HttpPost("{id}/cancel")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Cancel(string id, CancellationToken ct) =>
        await tasks.CancelAsync(access.TenantId, id, ct) is null ? NotFound() : NoContent();

    /// <summary>The aggregated final result (CLAUDE.md section 52): the root's own summary plus
    /// every other agent's completion summary, every artifact produced, and run metrics — not
    /// just the one-line root summary <see cref="Get"/> returns.</summary>
    [HttpGet("{id}/result")]
    public async Task<IActionResult> Result(string id, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == id && t.TenantId == access.TenantId, ct);
        if (task is null) return NotFound();

        if (task.ResultJson is null)
        {
            return Ok(new { ready = false, status = task.Status });
        }

        using var doc = JsonDocument.Parse(task.ResultJson);
        return Ok(new { ready = true, result = doc.RootElement.Clone() });
    }

    [HttpGet("{id}/artifacts")]
    public async Task<IActionResult> Artifacts(string id, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == id)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new
            {
                artifact_id = a.ArtifactId,
                type = a.Type,
                file_name = Path.GetFileName(a.Location),
                created_by_agent = a.CreatedByAgent,
                created_at = a.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(artifacts);
    }

    /// <summary>Streams an artifact's file content. Only files under the task's own sandboxed
    /// workspace directory can ever be referenced here (see WorkspacePath in the filesystem tools),
    /// so this can't be used to read arbitrary paths on the API container.</summary>
    [HttpGet("{id}/artifacts/{artifactId}/content")]
    public async Task<IActionResult> ArtifactContent(string id, string artifactId, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        var artifact = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TaskId == id && a.ArtifactId == artifactId, ct);
        if (artifact is null) return NotFound();

        if (!System.IO.File.Exists(artifact.Location))
        {
            return NotFound(new { error = "Artifact file no longer exists on disk." });
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(artifact.Location, ct);
        return File(bytes, "application/octet-stream", Path.GetFileName(artifact.Location));
    }

    /// <summary>
    /// Every artifact file the task produced, as one zip. Entries keep their folder layout relative
    /// to the task workspace (e.g. src/main.py). A file an agent wrote several times appears once,
    /// with its final contents. Only files inside this task's own workspace are included.
    /// </summary>
    [HttpGet("{id}/artifacts.zip")]
    public async Task<IActionResult> ArtifactsZip(string id, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        var locations = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == id)
            .Select(a => a.Location)
            .Distinct()
            .ToListAsync(ct);

        var opts = toolsOptions.Value;
        var files = locations
            .Where(l => WorkspacePath.IsInsideTaskRoot(opts, id, l) && System.IO.File.Exists(l))
            .ToList();
        if (files.Count == 0)
        {
            return NotFound(new { error = "This task has no artifact files to download." });
        }

        var buffer = await ArtifactFiles.ZipAsync(files, WorkspacePath.TaskRoot(opts, id), ct);
        return File(buffer, "application/zip", $"task-{id[..Math.Min(8, id.Length)]}-artifacts.zip");
    }

    [HttpGet("{id}/events")]
    public async Task<IActionResult> Events(string id, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (!await access.ScopeAsync(id, ct)) return NotFound();
        var events = await db.Events.AsNoTracking()
            .Where(e => e.TaskId == id && e.TenantId == access.TenantId)
            .OrderBy(e => e.Timestamp)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);

        return Ok(events);
    }
}
