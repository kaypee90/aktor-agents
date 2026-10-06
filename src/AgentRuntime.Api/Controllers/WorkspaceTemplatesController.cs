using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Integrations;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>Ready-made workspaces (docs/incident-response.md).</summary>
[ApiController]
public sealed class WorkspaceTemplatesController(IGrainFactory grains, AgentDbContext db) : ControllerBase
{
    public sealed record FromTemplateBody(string Template, string? Name, bool UseDemoSystem = true);

    [HttpGet("api/workspace-templates")]
    public IActionResult List() => Ok(WorkspaceTemplates.All.Select(t => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        goal = t.Goal,
        autonomy = t.Safety.Autonomy.ToString(),
        connections = t.Connections.Select(c => new { plugin_id = c.PluginId, name = c.Name, demo_only = c.DemoOnly }),
        webhooks = t.Webhooks.Select(w => new { name = w.Name, sample_payload = w.SamplePayload })
    }));

    /// <summary>
    /// Creates a workspace from a template: its goal, its pipeline, its safety policy (in force
    /// before the first run), its webhooks, and, with use_demo_system, the simulated
    /// connections it needs to be tried right away. Returns the webhook URLs (they hold a secret).
    /// </summary>
    [HttpPost("api/workspaces/from-template")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Create([FromBody] FromTemplateBody body, CancellationToken ct)
    {
        if (WorkspaceTemplates.Get(body.Template) is not { } template) return NotFound(new { error = $"No template '{body.Template}'." });

        var caller = HttpContext.Caller();
        var plan = await grains.GetGrain<Tenancy.ITenantGrain>(caller.TenantId).GetPlan();
        if (plan.MaxWorkspaces > 0 &&
            await db.Workspaces.CountAsync(w => w.TenantId == caller.TenantId && w.Status != "Archived", ct) >= plan.MaxWorkspaces)
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

        return Ok(new { workspace_id = id, template = template.Id, connections, webhooks });
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
