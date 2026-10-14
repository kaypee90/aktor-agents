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
    /// <summary>Without a pipeline, one is drafted from the goal by the pipeline editor. <c>Blank</c>
    /// starts with an empty canvas instead, and then the goal is optional.</summary>
    public sealed record CreateWorkspaceBody(string Name, string? Goal, int? DailyTokenLimit, decimal? DailyCostLimitUsd, PipelineDefinition? Pipeline = null,
        WorkspaceVisibility Visibility = WorkspaceVisibility.Organization, bool Blank = false);
    public sealed record VisibilityBody(WorkspaceVisibility Visibility);
    public sealed record PipelineBody(PipelineDefinition Pipeline, int BaseVersion, string? Note);
    public sealed record ProposeBody(string Request);
    public sealed record EditsBody(List<PipelineEditOp> Ops, int BaseVersion, string? Note);
    public sealed record RestoreBody(int Version);
    public sealed record LayoutBody(Dictionary<string, StagePosition>? Layout);
    /// <summary>With <c>data</c> (any JSON), action and script stages see it as <c>input</c>.</summary>
    public sealed record RunBody(string Input, JsonElement? Data = null);
    public sealed record FormSubmitBody(JsonElement? Answers, string? SubmissionId);
    public sealed record ScriptTestBody(string Language, string Code, JsonElement? Data, int? TimeoutSeconds);
    public sealed record MessageBody(string Text, string? ToAgentId, string? ClientMessageId);
    public sealed record CloneBody(string? Name, bool CopySkillsAndKnowledge = true);
    public sealed record ExportTemplateBody(string? Name, string? Description, string? Category);
    public sealed record WatchConditionBody(string Field, string Op, string? Value);
    public sealed record TriggerBody(string Kind, string Name, string? Instruction, double? EveryMinutes, string? Cron,
        // Watches (kind "watch"): a read-only connection tool, where the items are, and the rule.
        string? SourceTool = null, JsonElement? SourceArguments = null, string? ItemsPath = null, List<WatchConditionBody>? Conditions = null,
        string? KeyField = null, List<string>? DisplayFields = null, string? Mode = null, string? Message = null, string? Urgency = null,
        // Forms (kind "form"): the fields to fill in.
        FormDefinition? Form = null);
    public sealed record BudgetBody(int? DailyTokenLimit, decimal? DailyCostLimitUsd);
    public sealed record VariablesBody(Dictionary<string, string>? Variables);

    private IWorkspaceGrain Workspace(string id) => grains.GetGrain<IWorkspaceGrain>(id);

    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Goal) && !body.Blank) return BadRequest(new { error = "goal is required" });
        var name = string.IsNullOrWhiteSpace(body.Name) ? "Workspace" : body.Name;
        var goal = string.IsNullOrWhiteSpace(body.Goal) ? name : body.Goal;

        // The organization's plan limits how many workspaces can be live at once.
        var caller = HttpContext.Caller();
        var plan = await grains.GetGrain<AgentRuntime.Tenancy.ITenantGrain>(caller.TenantId).GetPlan();
        if (plan.MaxWorkspaces > 0 &&
            await db.Workspaces.CountAsync(w => w.TenantId == caller.TenantId && w.Status != "Archived" && w.Kind != "study", ct) >= plan.MaxWorkspaces)
        {
            return StatusCode(402, new { error = $"The {plan.Name} plan allows {plan.MaxWorkspaces} active workspaces. Archive one or upgrade." });
        }

        // Configured by natural language: the goal describes what the workspace is for, and the
        // editor drafts the pipeline from it. If it can't, the workspace starts with one stage.
        var pipeline = body.Blank ? new PipelineDefinition() : body.Pipeline;
        string? draftNote = null;
        if (pipeline is null)
        {
            var draft = await designer.ProposeAsync(caller.TenantId, null, goal, null, goal, ct);
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
            Name = name,
            Goal = goal,
            DailyTokenLimit = body.DailyTokenLimit,
            DailyCostLimitUsd = body.DailyCostLimitUsd,
            TenantId = caller.TenantId,
            OwnerId = caller.ActorId,
            CreatedBy = caller.ActorId,
            Pipeline = pipeline,
            Visibility = body.Visibility
        });
        return Ok(new { workspace_id = id, pipeline = await Workspace(id).GetPipeline(), draft = draftNote });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // Other people's private workspaces aren't listed.
        var hidden = await access.HiddenWorkspacesAsync(ct);
        return Ok(await db.Workspaces.AsNoTracking()
            // A study's workspace belongs to the study (docs/studies.md), not to this list.
            .Where(w => w.TenantId == access.TenantId && w.Kind != "study" && !hidden.Contains(w.WorkspaceId))
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
                updated_at = w.UpdatedAt,
                visibility = w.Visibility
            })
            .ToListAsync(ct));
    }

    /// <summary>Makes a workspace private to its owner, or visible to the organization. Only the
    /// owner may change it (an admin who can see an organization-visible workspace still can't hide
    /// it from its owner's colleagues on the owner's behalf).</summary>
    [HttpPut("{id}/visibility")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> SetVisibility(string id, [FromBody] VisibilityBody body)
    {
        if (!await access.OwnsWorkspaceAsync(id)) return StatusCode(403, new { error = "Only the workspace's owner can change who sees it." });
        await Workspace(id).SetVisibility(body.Visibility, HttpContext.Caller().ActorId);
        return Ok(new { visibility = body.Visibility });
    }

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
        var data = body.Data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } d ? d.GetRawText() : null;
        var input = string.IsNullOrWhiteSpace(body.Input) && data is not null ? data : body.Input;
        var result = data is null
            ? await Workspace(id).StartRun(input, HttpContext.Caller().ActorId)
            : await Workspace(id).StartRunWithData(input, data, HttpContext.Caller().ActorId);
        return result.Success ? Ok(result) : BadRequest(new { error = result.Message });
    }

    // ---- Forms ------------------------------------------------------------------------

    /// <summary>A form trigger's form, for its page. Members of the workspace's organization only.</summary>
    [HttpGet("{id}/forms/{triggerId}")]
    public async Task<IActionResult> Form(string id, string triggerId) =>
        WorkspaceIds.IsWorkspace(id) && await Workspace(id).GetForm(triggerId) is { } form ? Ok(form) : NotFound();

    /// <summary>Submits a form: the answers are checked against its fields, then a run starts with them.</summary>
    [HttpPost("{id}/forms/{triggerId}/submissions")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> SubmitForm(string id, string triggerId, [FromBody] FormSubmitBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        var caller = HttpContext.Caller();
        // Forms are for people signed in to the organization; automation starts runs with /runs.
        if (caller.IsApiKey) return StatusCode(403, new { error = "Forms are submitted by signed-in people. Use POST /api/workspaces/{id}/runs with data from code." });
        var result = await Workspace(id).SubmitForm(new FormSubmission
        {
            TriggerId = triggerId,
            AnswersJson = body.Answers is { ValueKind: JsonValueKind.Object } a ? a.GetRawText() : "{}",
            SubmittedBy = caller.ActorId,
            SubmittedByName = caller.Email,
            SubmissionId = body.SubmissionId is { Length: > 0 and <= 100 } sid ? sid : null
        });
        if (result.NotFound) return NotFound();
        return result.Success ? Ok(result) : BadRequest(new { error = result.Message, field_errors = result.FieldErrors });
    }

    // ---- Script stages ----------------------------------------------------------------

    /// <summary>Runs a script stage's code in the sandbox with sample data, to try it before saving.</summary>
    [HttpPost("{id}/scripts/test")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> TestScript(string id, [FromBody] ScriptTestBody body, [FromServices] PipelineStepExecutor steps, CancellationToken ct)
    {
        if (!WorkspaceIds.IsWorkspace(id) || await Workspace(id).GetSnapshot() is null) return NotFound();
        if (!ScriptLanguages.IsKnown(body.Language)) return BadRequest(new { error = $"language must be {string.Join(" or ", ScriptLanguages.All)}" });
        if (string.IsNullOrWhiteSpace(body.Code) || body.Code.Length > pipelineOptions.Value.MaxCodeLength) return BadRequest(new { error = "code is empty or too long" });
        var data = body.Data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } d ? d.GetRawText() : "{}";
        var result = await steps.TestScriptAsync(body.Language, body.Code, data, body.TimeoutSeconds, ct);
        JsonElement? output = null;
        if (result.Ok)
        {
            using var doc = JsonDocument.Parse(result.OutputJson);
            output = doc.RootElement.Clone();
        }

        return Ok(new { ok = result.Ok, output, error = result.Error, logs = result.Logs, duration_ms = result.DurationMs });
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
        if (!Enum.TryParse<TriggerKind>(body.Kind, ignoreCase: true, out var kind)) return BadRequest(new { error = "kind must be schedule, webhook, watch or form" });

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
            Urgency = body.Urgency,
            Form = body.Form
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

    /// <summary>Replaces the workspace's variables: values every stage can use as {{ vars.name }}.</summary>
    [HttpPut("{id}/variables")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Variables(string id, [FromBody] VariablesBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        var errors = await Workspace(id).SetVariables(body.Variables ?? [], HttpContext.Caller().ActorId);
        return errors.Count > 0
            ? BadRequest(new { error = string.Join(" ", errors), errors })
            : Ok(new { variables = (await Workspace(id).GetSnapshot())?.Variables ?? [] });
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

    // ---- Copies, templates and deletion (docs/workspaces.md) ----

    /// <summary>A new workspace with this one's setup: goal, pipeline, safety policy, budget, triggers
    /// (with new webhook URLs), skills and knowledge, and for an Admin its connections with their
    /// secrets. Runs, files and chat are never copied.</summary>
    [HttpPost("{id}/clone")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Clone(string id, [FromBody] CloneBody? body, [FromServices] AgentRuntime.Api.Platform.WorkspaceCopyService copies, CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        try
        {
            // The owner of a private workspace has an admin's say over it (no admin can see it).
            var admin = caller.Role >= AgentRuntime.Tenancy.TenantRole.Admin ||
                        ((await Workspace(id).GetAccessInfo())?.Visibility == WorkspaceVisibility.Private && await access.OwnsWorkspaceAsync(id));
            return Ok(await copies.CloneAsync(access.TenantId, id, body?.Name, admin, body?.CopySkillsAndKnowledge ?? true, caller.ActorId, ct));
        }
        catch (AgentRuntime.Api.Platform.WorkspaceCopyException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    /// <summary>Saves the workspace as one of the organization's templates (no secrets, chat, runs or files).</summary>
    [HttpPost("{id}/export-template")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> ExportTemplate(string id, [FromBody] ExportTemplateBody? body, [FromServices] AgentRuntime.Api.Platform.WorkspaceCopyService copies, CancellationToken ct)
    {
        try
        {
            var template = await copies.ExportTemplateAsync(access.TenantId, id, body?.Name, body?.Description, body?.Category, HttpContext.Caller().ActorId, ct);
            return Ok(new { template_id = template.TemplateId, name = template.Name, category = template.Category });
        }
        catch (AgentRuntime.Api.Platform.WorkspaceCopyException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    /// <summary>Deletes an archived workspace with its connections, secrets, knowledge, skills and
    /// files. Its runs' history and the audit log stay.</summary>
    [HttpDelete("{id}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> Delete(string id, [FromServices] AgentRuntime.Api.Platform.WorkspaceCopyService copies, CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        try
        {
            await copies.DeleteAsync(access.TenantId, id, caller.Email ?? caller.ActorId, ct);
            return NoContent();
        }
        catch (AgentRuntime.Api.Platform.WorkspaceCopyException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

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
