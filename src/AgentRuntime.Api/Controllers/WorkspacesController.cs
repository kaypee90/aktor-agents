using System.Text.Json;
using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Pipelines;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Workspaces: reusable agent pipelines, configured in plain language or on the canvas, run by
/// people and by triggers (schedules, webhooks, watches). See docs/workspaces.md.
/// </summary>
[ApiController]
[Route("api/workspaces")]
[AgentRuntime.Api.Platform.WorkspaceAccess]
public sealed class WorkspacesController(IGrainFactory grains, AgentDbContext db, AgentRuntime.Api.Platform.TenantAccess access,
    Microsoft.Extensions.Options.IOptions<AgentRuntime.Infrastructure.Tools.ToolsOptions> toolsOptions, PipelineDesignService designer,
    Microsoft.Extensions.Options.IOptions<PipelineOptions> pipelineOptions) : ControllerBase
{
    /// <summary>Without a pipeline, one is drafted from the goal by the pipeline editor.</summary>
    public sealed record CreateWorkspaceBody(string Name, string Goal, int? DailyTokenLimit, decimal? DailyCostLimitUsd, PipelineDefinition? Pipeline = null);
    public sealed record PipelineBody(PipelineDefinition Pipeline, int BaseVersion, string? Note);
    public sealed record ProposeBody(string Request);
    public sealed record EditsBody(List<PipelineEditOp> Ops, int BaseVersion, string? Note);
    public sealed record RestoreBody(int Version);
    public sealed record LayoutBody(Dictionary<string, StagePosition>? Layout);
    public sealed record RunBody(string Input);
    public sealed record MessageBody(string Text, string? ToAgentId, string? ClientMessageId);
    public sealed record WatchConditionBody(string Field, string Op, string? Value);
    public sealed record TriggerBody(string Kind, string Name, string? Instruction, double? EveryMinutes, string? Cron,
        // Watches (kind "watch"): a read-only connection tool, where the items are, and the rule.
        string? SourceTool = null, JsonElement? SourceArguments = null, string? ItemsPath = null, List<WatchConditionBody>? Conditions = null,
        string? KeyField = null, List<string>? DisplayFields = null, string? Mode = null, string? Message = null, string? Urgency = null);
    public sealed record BudgetBody(int? DailyTokenLimit, decimal? DailyCostLimitUsd);

    private IWorkspaceGrain Workspace(string id) => grains.GetGrain<IWorkspaceGrain>(id);

    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Goal)) return BadRequest(new { error = "goal is required" });

        // The organization's plan limits how many workspaces can be live at once.
        var caller = HttpContext.Caller();
        var plan = await grains.GetGrain<AgentRuntime.Tenancy.ITenantGrain>(caller.TenantId).GetPlan();
        if (plan.MaxWorkspaces > 0 &&
            await db.Workspaces.CountAsync(w => w.TenantId == caller.TenantId && w.Status != "Archived", ct) >= plan.MaxWorkspaces)
        {
            return StatusCode(402, new { error = $"The {plan.Name} plan allows {plan.MaxWorkspaces} active workspaces. Archive one or upgrade." });
        }

        // Configured by natural language: the goal describes what the workspace is for, and the
        // editor drafts the pipeline from it. If it can't, the workspace starts with one stage.
        var pipeline = body.Pipeline;
        string? draftNote = null;
        if (pipeline is null)
        {
            var draft = await designer.ProposeAsync(caller.TenantId, null, body.Goal, null, body.Goal, ct);
            pipeline = draft.Valid ? draft.Preview : null;
            draftNote = draft.Valid ? draft.Summary : string.Join(" ", draft.Errors);
        }
        else if (PipelineValidator.Validate(pipeline, pipelineOptions.Value) is { Count: > 0 } errors)
        {
            return BadRequest(new { error = "The pipeline can't run.", errors });
        }

        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? "Workspace" : body.Name,
            Goal = body.Goal,
            DailyTokenLimit = body.DailyTokenLimit,
            DailyCostLimitUsd = body.DailyCostLimitUsd,
            TenantId = caller.TenantId,
            OwnerId = caller.ActorId,
            CreatedBy = caller.ActorId,
            Pipeline = pipeline
        });
        return Ok(new { workspace_id = id, pipeline = await Workspace(id).GetPipeline(), draft = draftNote });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await db.Workspaces.AsNoTracking()
            .Where(w => w.TenantId == access.TenantId)
            .OrderByDescending(w => w.CreatedAt)
            .Take(100)
            .Select(w => new
            {
                workspace_id = w.WorkspaceId,
                name = w.Name,
                goal = w.Goal,
                status = w.Status,
                agents = w.Agents,
                triggers = w.Triggers,
                total_tokens = w.TotalTokens,
                total_cost_usd = w.TotalCostUsd,
                created_at = w.CreatedAt,
                updated_at = w.UpdatedAt
            })
            .ToListAsync(ct));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        var snapshot = await Workspace(id).GetSnapshot();
        return snapshot is null ? NotFound() : Ok(snapshot);
    }

    [HttpPost("{id}/messages")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> PostMessage(string id, [FromBody] MessageBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id) || await Workspace(id).GetSnapshot() is null) return NotFound();
        try
        {
            return Ok(await Workspace(id).PostUserMessage(body.Text, body.ToAgentId, body.ClientMessageId, HttpContext.Caller().ActorId));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ---- Pipeline -------------------------------------------------------------------

    [HttpGet("{id}/pipeline")]
    public async Task<IActionResult> Pipeline(string id) =>
        WorkspaceIds.IsWorkspace(id) && await Workspace(id).GetPipeline() is { } pipeline ? Ok(pipeline) : NotFound();

    [HttpGet("{id}/pipeline/history")]
    public async Task<IActionResult> PipelineHistory(string id) =>
        WorkspaceIds.IsWorkspace(id) ? Ok(await Workspace(id).GetPipelineHistory()) : NotFound();

    /// <summary>Replaces the pipeline (the canvas's save). 409 if it changed since base_version.</summary>
    [HttpPut("{id}/pipeline")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> SavePipeline(string id, [FromBody] PipelineBody body) =>
        ChangeResult(await Workspace(id).SetPipeline(body.Pipeline, body.BaseVersion, HttpContext.Caller().ActorId, body.Note ?? string.Empty));

    /// <summary>Asks the editor for the changes a plain-language request means. Changes nothing:
    /// apply the returned ops with POST pipeline/edits.</summary>
    [HttpPost("{id}/pipeline/propose")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Propose(string id, [FromBody] ProposeBody body, CancellationToken ct)
    {
        var snapshot = await Workspace(id).GetSnapshot();
        if (snapshot is null) return NotFound();
        return Ok(await designer.ProposeAsync(access.TenantId, id, snapshot.Goal, snapshot.Pipeline, body.Request, ct));
    }

    /// <summary>Applies edits (a proposal's, or the canvas's) to the pipeline at base_version.</summary>
    [HttpPost("{id}/pipeline/edits")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> ApplyEdits(string id, [FromBody] EditsBody body, CancellationToken ct)
    {
        // A stage's model must be one the organization set up (an empty id means the workspace's).
        var models = body.Ops.Select(o => o.Stage?.ModelProfileId).Where(m => !string.IsNullOrEmpty(m)).ToList();
        if (models.Count > 0)
        {
            var known = (await designer.ModelsAsync(access.TenantId, ct)).Select(m => m.Id).ToList();
            if (models.FirstOrDefault(m => !known.Contains(m!)) is { } unknown)
                return BadRequest(new PipelineChangeResult { Errors = [$"There's no model '{unknown}'. Use one of: {string.Join(", ", known)}."] });
        }

        return ChangeResult(await Workspace(id).ApplyPipelineEdits(body.Ops, body.BaseVersion, HttpContext.Caller().ActorId, body.Note));
    }

    /// <summary>Places stages on the canvas (no new version). `{layout: {stage_id: {x, y}}}`; empty
    /// lays the pipeline out automatically again.</summary>
    [HttpPut("{id}/pipeline/layout")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> SaveLayout(string id, [FromBody] LayoutBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        if (body.Layout is { Count: > 200 }) return BadRequest(new { error = "Too many positions." });
        return await Workspace(id).SetPipelineLayout(body.Layout ?? []) is { } pipeline ? Ok(pipeline) : NotFound();
    }

    [HttpPost("{id}/pipeline/restore")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Restore(string id, [FromBody] RestoreBody body) =>
        ChangeResult(await Workspace(id).RestorePipelineVersion(body.Version, HttpContext.Caller().ActorId));

    private IActionResult ChangeResult(PipelineChangeResult result) =>
        result.Success ? Ok(result) : result.Conflict ? Conflict(result) : BadRequest(result);

    // ---- Runs -------------------------------------------------------------------------

    /// <summary>Runs the pipeline with an input. The run is a task: follow it at /api/tasks/{run_id}.</summary>
    [HttpPost("{id}/runs")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> StartRun(string id, [FromBody] RunBody body)
    {
        var result = await Workspace(id).StartRun(body.Input, HttpContext.Caller().ActorId);
        return result.Success ? Ok(result) : BadRequest(new { error = result.Message });
    }

    [HttpGet("{id}/runs")]
    public async Task<IActionResult> Runs(string id) =>
        await Workspace(id).GetSnapshot() is { } snapshot ? Ok(snapshot.Runs) : NotFound();

    /// <summary>A run with each stage's status, result and agent.</summary>
    [HttpGet("{id}/runs/{runId}")]
    public async Task<IActionResult> Run(string id, string runId) =>
        await RunOf(id, runId) is { } run ? Ok(await run.GetView()) : NotFound();

    [HttpPost("{id}/runs/{runId}/{command:regex(^(pause|resume|cancel)$)}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> ControlRun(string id, string runId, string command)
    {
        if (await RunOf(id, runId) is not { } run) return NotFound();
        await (command switch
        {
            "pause" => run.Pause(),
            "resume" => run.Resume(),
            _ => run.Cancel("cancelled by a person")
        });
        return Ok(await run.GetView());
    }

    private async Task<IPipelineRunGrain?> RunOf(string workspaceId, string runId)
    {
        if (!PipelineIds.IsRun(runId)) return null;
        var run = grains.GetGrain<IPipelineRunGrain>(runId);
        return (await run.GetView())?.WorkspaceId == workspaceId ? run : null;
    }

    [HttpGet("{id}/triggers")]
    public async Task<IActionResult> Triggers(string id) =>
        WorkspaceIds.IsWorkspace(id) ? Ok(await Workspace(id).ListTriggers()) : NotFound();

    /// <summary>Creates a trigger as the user. For a webhook, the response includes its secret
    /// path — the only time (besides the workspace chat) it is shown.</summary>
    [HttpPost("{id}/triggers")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> AddTrigger(string id, [FromBody] TriggerBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        if (!Enum.TryParse<TriggerKind>(body.Kind, ignoreCase: true, out var kind)) return BadRequest(new { error = "kind must be schedule, webhook or watch" });

        var result = await Workspace(id).AddTrigger(new TriggerSpec
        {
            Kind = kind,
            Name = body.Name,
            Instruction = body.Instruction ?? string.Empty,
            EveryMinutes = body.EveryMinutes,
            Cron = body.Cron,
            SourceTool = body.SourceTool,
            SourceArgumentsJson = body.SourceArguments is { ValueKind: JsonValueKind.Object } a ? a.GetRawText() : null,
            Rule = kind == TriggerKind.Watch
                ? new WatchRule
                {
                    ItemsPath = body.ItemsPath ?? string.Empty,
                    Conditions = (body.Conditions ?? []).Select(c => new WatchCondition { Field = c.Field, Op = c.Op, Value = c.Value }).ToList(),
                    KeyField = body.KeyField,
                    DisplayFields = body.DisplayFields ?? []
                }
                : null,
            WatchMode = body.Mode,
            MessageTemplate = body.Message,
            Urgency = body.Urgency
        }, "user", idempotencyKey: string.Empty, revealSecret: true);

        return result.Success
            ? Content(result.ResultJson ?? "{}", "application/json")
            : BadRequest(new { error = result.Message });
    }

    [HttpDelete("{id}/triggers/{triggerId}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> RemoveTrigger(string id, string triggerId)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        await Workspace(id).RemoveTrigger(triggerId, "user");
        return NoContent();
    }

    [HttpPut("{id}/budget")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> Budget(string id, [FromBody] BudgetBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        await Workspace(id).UpdateBudget(body.DailyTokenLimit, body.DailyCostLimitUsd);
        return NoContent();
    }

    [HttpPost("{id}/pause")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Pause(string id) { await Workspace(id).Pause(); return NoContent(); }

    [HttpPost("{id}/resume")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Resume(string id) { await Workspace(id).Resume(); return NoContent(); }

    [HttpPost("{id}/archive")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> Archive(string id) { await Workspace(id).Archive(); return NoContent(); }

    // ---- Files: what the workspace's agents saved with filesystem_write ----

    /// <summary>One entry per file the workspace's agents wrote, newest first, with who wrote it.</summary>
    /// <summary>The workspace's files: its own and every run's (under run-&lt;number&gt;/).</summary>
    [HttpGet("{id}/files")]
    public async Task<IActionResult> Files(string id, CancellationToken ct)
    {
        var files = await WorkspaceFilesAsync(id, ct);
        return Ok(files.Select(f => new
        {
            artifact_id = f.Latest.ArtifactId,
            path = f.RelativePath,
            file_name = Path.GetFileName(f.RelativePath),
            size_bytes = f.SizeBytes,
            versions = f.Versions,
            created_by_agent = f.Latest.CreatedByAgent,
            updated_at = f.Latest.CreatedAt
        }));
    }

    /// <summary>A file's current contents. Any recorded write of the file identifies it.</summary>
    [HttpGet("{id}/files/{artifactId}/content")]
    public async Task<IActionResult> FileContent(string id, string artifactId, CancellationToken ct)
    {
        if (await FileOfAsync(id, artifactId, ct) is not { } artifact) return NotFound();
        return PhysicalFile(Path.GetFullPath(artifact.Location),
            AgentRuntime.Infrastructure.Documents.DocumentFormats.ContentTypeOf(artifact.Location), Path.GetFileName(artifact.Location));
    }

    /// <summary>A file shown in place: see <see cref="AgentRuntime.Api.Platform.FilePreviews"/>.</summary>
    [HttpGet("{id}/files/{artifactId}/preview")]
    public async Task<IActionResult> FilePreview(string id, string artifactId, CancellationToken ct)
    {
        if (await FileOfAsync(id, artifactId, ct) is not { } artifact) return NotFound();
        var relative = Path.GetRelativePath(AgentRuntime.Infrastructure.Tools.WorkspacePath.TaskRoot(toolsOptions.Value, artifact.TaskId), artifact.Location)
            .Replace(Path.DirectorySeparatorChar, '/');
        return Ok(await AgentRuntime.Api.Platform.FilePreviews.BuildAsync(artifact.Location, relative, artifact.CreatedByAgent, artifact.CreatedAt, ct));
    }

    /// <summary>Every file as one zip, keeping the folders agents used (each run's under run-&lt;number&gt;/).</summary>
    [HttpGet("{id}/files.zip")]
    public async Task<IActionResult> FilesZip(string id, CancellationToken ct)
    {
        var files = await WorkspaceFilesAsync(id, ct);
        if (files.Count == 0) return NotFound(new { error = "This workspace has no files yet." });

        var zip = await AgentRuntime.Api.Platform.ArtifactFiles.ZipAsync(files.Select(f => (f.Latest.Location, f.RelativePath)), ct);
        return File(zip, "application/zip", $"{id}-files.zip");
    }

    private async Task<List<AgentRuntime.Api.Platform.ArtifactFiles.FileView>> WorkspaceFilesAsync(string id, CancellationToken ct)
    {
        var runs = (await Workspace(id).GetSnapshot())?.Runs ?? [];
        return await AgentRuntime.Api.Platform.ArtifactFiles.ListForWorkspaceAsync(db, toolsOptions.Value, id,
            runs.ToDictionary(r => r.RunId, r => r.Number), access.TenantId, ct);
    }

    /// <summary>A recorded file of the workspace or one of its runs, still on disk inside its sandbox.</summary>
    private async Task<ArtifactRecord?> FileOfAsync(string id, string artifactId, CancellationToken ct)
    {
        var scopes = await AgentRuntime.Api.Platform.ArtifactFiles.ScopesOfWorkspaceAsync(db, id, access.TenantId, ct);
        var artifact = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => scopes.Contains(a.TaskId) && a.ArtifactId == artifactId && a.TenantId == access.TenantId, ct);
        return artifact is not null &&
               AgentRuntime.Infrastructure.Tools.WorkspacePath.IsInsideTaskRoot(toolsOptions.Value, artifact.TaskId, artifact.Location) &&
               System.IO.File.Exists(artifact.Location)
            ? artifact
            : null;
    }
}

/// <summary>
/// Public inbound webhook endpoint for workspace triggers. Authenticated by the secret in the URL
/// (constant-time comparison); acknowledged with 202 only after the event is durably queued for the
/// agent, so a sender that gets 202 knows it was received. Redeliveries are dropped by delivery id.
/// </summary>
[ApiController]
[Route("api/hooks")]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class HooksController(IGrainFactory grains, Microsoft.Extensions.Options.IOptions<WorkspaceOptions> options) : ControllerBase
{
    private static readonly string[] DeliveryIdHeaders =
        ["Idempotency-Key", "X-Idempotency-Key", "X-Shopify-Webhook-Id", "X-GitHub-Delivery", "X-Request-Id", "Webhook-Id", "X-Delivery-Id"];

    [HttpPost("{workspaceId}/{triggerId}/{token}")]
    public async Task<IActionResult> Receive(string workspaceId, string triggerId, string token)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId)) return NotFound();

        var max = options.Value.MaxWebhookBodyBytes;
        if (Request.ContentLength > max) return StatusCode(413);

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            var buffer = new char[max + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > max) return StatusCode(413);
            body = new string(buffer, 0, read);
        }

        var deliveryId = DeliveryIdHeaders.Select(h => Request.Headers[h].ToString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var outcome = await grains.GetGrain<IWorkspaceGrain>(workspaceId).DeliverWebhook(new WebhookDelivery
        {
            TriggerId = triggerId,
            Token = token,
            Body = body,
            DeliveryId = deliveryId,
            ContentType = Request.ContentType
        });

        return outcome switch
        {
            WebhookOutcome.Accepted => Accepted(new { status = "accepted" }),
            WebhookOutcome.Duplicate => Ok(new { status = "duplicate" }),
            WebhookOutcome.RateLimited => StatusCode(429, new { status = "rate_limited" }),
            WebhookOutcome.Inactive => StatusCode(409, new { status = "workspace_inactive" }),
            // Same answer for an unknown trigger and a wrong secret, so the endpoint doesn't reveal
            // which trigger ids exist.
            _ => NotFound()
        };
    }
}
