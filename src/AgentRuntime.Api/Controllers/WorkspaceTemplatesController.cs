using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Integrations;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>Ready-made workspaces: real-world pipelines to start from (docs/templates.md).</summary>
[ApiController]
public sealed class WorkspaceTemplatesController(IGrainFactory grains, AgentDbContext db, WorkspaceCopyService copies) : ControllerBase
{
    public sealed record FromTemplateBody(string Template, string? Name, bool UseDemoSystem = true);

    /// <summary>The built-in templates, then the organization's own (made from its workspaces).</summary>
    [HttpGet("api/workspace-templates")]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(WorkspaceTemplates.All.Select(t => (object)new
    {
        id = t.Id,
        name = t.Name,
        category = t.Category,
        description = t.Description,
        goal = t.Goal,
        autonomy = t.Safety.Autonomy.ToString(),
        // What it would try first: a webhook's sample payload goes through the real webhook.
        sample_input = t.SampleInput ?? t.Webhooks.FirstOrDefault()?.SamplePayload,
        stages = t.Pipeline.Stages.Select(s => new { stage_id = s.StageId, name = s.Name, inputs = s.Inputs }),
        connections = t.Connections.Select(c => new { plugin_id = c.PluginId, name = c.Name, demo_only = c.DemoOnly }),
        webhooks = t.Webhooks.Select(w => new { name = w.Name, sample_payload = w.SamplePayload }),
        schedules = t.Schedules.Select(s => new { name = s.Name, cron = s.Cron }),
        custom = false
    }).Concat((await copies.ListTemplatesAsync(HttpContext.Caller().TenantId, ct)).Select(Custom)));

    private static object Custom(Infrastructure.Persistence.OrganizationTemplateRecord t)
    {
        var d = WorkspaceCopyService.DefinitionOf(t);
        return new
        {
            id = t.TemplateId,
            name = t.Name,
            category = t.Category,
            description = t.Description,
            goal = d.Goal,
            autonomy = d.SafetyPolicy.Autonomy.ToString(),
            sample_input = t.SampleInput,
            stages = (d.Pipeline?.Stages ?? []).Select(s => new { stage_id = s.StageId, name = s.Name, inputs = s.Inputs }),
            connections = d.Connections.Select(c => new { plugin_id = c.PluginId, name = c.Name, demo_only = false }),
            webhooks = d.Triggers.Where(x => x.Kind == TriggerKind.Webhook).Select(x => new { name = x.Name, sample_payload = (string?)null }),
            schedules = d.Triggers.Where(x => x.Kind == TriggerKind.Schedule).Select(x => new { name = x.Name, cron = x.Cron }),
            custom = true,
            created_by = t.CreatedBy,
            created_at = t.CreatedAt
        };
    }

    /// <summary>Removes one of the organization's templates (workspaces made from it are unaffected).</summary>
    [HttpDelete("api/workspace-templates/{id}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct) =>
        await copies.DeleteTemplateAsync(HttpContext.Caller().TenantId, id, ct) ? NoContent() : NotFound(new { error = $"No template '{id}' of yours." });

    /// <summary>One of the organization's templates as a file, to keep or to import elsewhere.</summary>
    [HttpGet("api/workspace-templates/{id}/download")]
    public async Task<IActionResult> Download(string id, CancellationToken ct) =>
        await copies.GetTemplateAsync(HttpContext.Caller().TenantId, id, ct) is { } t
            ? File(System.Text.Encoding.UTF8.GetBytes(WorkspaceCopyService.ToFile(t)), "application/json", $"{ConnectionNames.Slug(t.Name)}.template.json")
            : NotFound(new { error = $"No template '{id}' of yours." });

    /// <summary>Adds a template from a downloaded template file (the file's JSON as the body).</summary>
    [HttpPost("api/workspace-templates/import")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        if (json.Length > 1_000_000) return BadRequest(new { error = "A template file is at most 1 MB." });
        try
        {
            var t = await copies.ImportTemplateAsync(HttpContext.Caller().TenantId, json, HttpContext.Caller().ActorId, ct);
            return Ok(Custom(t));
        }
        catch (WorkspaceCopyException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Creates a workspace from a template: its goal, its pipeline, its safety policy (in force
    /// before the first run), its webhooks and schedules, and, with use_demo_system, the simulated
    /// connections it needs to be tried right away. Returns the webhook URLs (they hold a secret).
    /// </summary>
    [HttpPost("api/workspaces/from-template")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Create([FromBody] FromTemplateBody body, CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        // One of the organization's own templates.
        if (body.Template.StartsWith(WorkspaceCopyService.TemplatePrefix, StringComparison.Ordinal))
        {
            if (await copies.GetTemplateAsync(caller.TenantId, body.Template, ct) is not { } own) return NotFound(new { error = $"No template '{body.Template}'." });
            try
            {
                var (workspaceId, ownConnections, triggers) = await copies.CreateFromDefinitionAsync(caller.TenantId, caller.ActorId,
                    WorkspaceCopyService.DefinitionOf(own), string.IsNullOrWhiteSpace(body.Name) ? own.Name : body.Name.Trim(), own.TemplateId,
                    addConnections: caller.Role >= Tenancy.TenantRole.Admin, ct);
                return Ok(new { workspace_id = workspaceId, template = own.TemplateId, connections = ownConnections, triggers, sample_input = own.SampleInput });
            }
            catch (WorkspaceCopyException ex)
            {
                return StatusCode(ex.Status, new { error = ex.Message });
            }
        }

        if (WorkspaceTemplates.Get(body.Template) is not { } template) return NotFound(new { error = $"No template '{body.Template}'." });

        var plan = await grains.GetGrain<Tenancy.ITenantGrain>(caller.TenantId).GetPlan();
        if (plan.MaxWorkspaces > 0 &&
            await db.Workspaces.CountAsync(w => w.TenantId == caller.TenantId && w.Status != "Archived" && w.Kind != "study", ct) >= plan.MaxWorkspaces)
        {
            return StatusCode(402, new { error = $"The {plan.Name} plan allows {plan.MaxWorkspaces} active workspaces. Archive one or upgrade." });
        }

        var id = WorkspaceIds.New();
        var workspace = grains.GetGrain<IWorkspaceGrain>(id);
        await workspace.Create(new WorkspaceCreationRequest
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? template.Name : body.Name,
            Goal = template.Goal,
            DailyTokenLimit = template.DailyTokenLimit,
            DailyCostLimitUsd = template.DailyCostLimitUsd,
            TenantId = caller.TenantId,
            OwnerId = caller.ActorId,
            TemplateId = template.Id,
            SafetyPolicy = template.Safety,
            Pipeline = template.Pipeline,
            CreatedBy = caller.ActorId
        });

        var connections = new List<object>();
        foreach (var c in template.Connections.Where(c => body.UseDemoSystem || !c.DemoOnly))
        {
            var result = await workspace.AddConnection(new ConnectionRequest { PluginId = c.PluginId, Name = c.Name });
            connections.Add(new { plugin_id = c.PluginId, name = c.Name, ok = result.Success, message = result.Message });
        }

        var webhooks = new List<object>();
        foreach (var w in template.Webhooks)
        {
            var added = await workspace.AddTrigger(
                new TriggerSpec { Kind = TriggerKind.Webhook, Name = w.Name, Instruction = w.Instruction },
                "user", $"template:{id}:{w.Name}", revealSecret: true);
            string? path = null;
            if (added.ResultJson is { } json)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                path = doc.RootElement.TryGetProperty("webhook_path", out var p) ? p.GetString() : null;
            }

            webhooks.Add(new { name = w.Name, ok = added.Success, message = added.Message, path });
        }

        var schedules = new List<object>();
        foreach (var s in template.Schedules)
        {
            var added = await workspace.AddTrigger(new TriggerSpec { Kind = TriggerKind.Schedule, Name = s.Name, Instruction = s.Instruction, Cron = s.Cron },
                "user", $"template:{id}:{s.Name}", revealSecret: false);
            schedules.Add(new { name = s.Name, ok = added.Success, message = added.Message });
        }

        return Ok(new { workspace_id = id, template = template.Id, connections, webhooks, schedules, sample_input = template.SampleInput });
    }

    /// <summary>Sends the template's sample alert through the workspace's own webhook, exactly as
    /// a monitoring system would (for demos and testing the setup).</summary>
    [HttpPost("api/workspaces/{id}/simulate-alert")]
    [Authorize(Policies.Member)]
    [WorkspaceAccess]
    public async Task<IActionResult> SimulateAlert(string id, [FromBody] SimulateAlertBody? body)
    {
        var workspace = grains.GetGrain<IWorkspaceGrain>(id);
        var snapshot = await workspace.GetSnapshot();
        if (snapshot?.TemplateId is null || WorkspaceTemplates.Get(snapshot.TemplateId) is not { } template || template.Webhooks.Count == 0)
        {
            return BadRequest(new { error = "This workspace wasn't made from a template with a webhook." });
        }

        var hook = template.Webhooks[0];
        var trigger = (await workspace.ListTriggers()).FirstOrDefault(t => t.Name == hook.Name && t.Kind == TriggerKind.Webhook);
        if (trigger is null) return BadRequest(new { error = $"The '{hook.Name}' webhook was removed." });

        var outcome = await workspace.DeliverWebhookAsOwner(trigger.TriggerId, body?.Payload ?? hook.SamplePayload, "simulated-" + Guid.NewGuid().ToString("n"));
        return outcome == WebhookOutcome.Accepted ? Accepted(new { delivered = true, trigger = trigger.Name }) : StatusCode(409, new { delivered = false, outcome = outcome.ToString() });
    }

    public sealed record SimulateAlertBody(string? Payload);
}
