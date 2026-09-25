using System.Text.Json;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Workspaces: long-running environments where a user's agents live, take commands at any time,
/// and are woken by schedules and webhooks (docs/workspaces.md).
/// </summary>
[ApiController]
[Route("api/workspaces")]
public sealed class WorkspacesController(IGrainFactory grains, AgentDbContext db) : ControllerBase
{
    public sealed record CreateWorkspaceBody(string Name, string Goal, int? DailyTokenLimit, decimal? DailyCostLimitUsd);
    public sealed record MessageBody(string Text, string? ToAgentId, string? ClientMessageId);
    public sealed record TriggerBody(string Kind, string Name, string? Instruction, string? TargetAgentId, double? EveryMinutes, string? Cron);
    public sealed record BudgetBody(int? DailyTokenLimit, decimal? DailyCostLimitUsd);

    private IWorkspaceGrain Workspace(string id) => grains.GetGrain<IWorkspaceGrain>(id);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Goal)) return BadRequest(new { error = "goal is required" });

        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? "Workspace" : body.Name,
            Goal = body.Goal,
            DailyTokenLimit = body.DailyTokenLimit,
            DailyCostLimitUsd = body.DailyCostLimitUsd
        });
        return Ok(new { workspace_id = id });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await db.Workspaces.AsNoTracking()
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
    public async Task<IActionResult> PostMessage(string id, [FromBody] MessageBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id) || await Workspace(id).GetSnapshot() is null) return NotFound();
        try
        {
            return Ok(await Workspace(id).PostUserMessage(body.Text, body.ToAgentId, body.ClientMessageId));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{id}/triggers")]
    public async Task<IActionResult> Triggers(string id) =>
        WorkspaceIds.IsWorkspace(id) ? Ok(await Workspace(id).ListTriggers()) : NotFound();

    /// <summary>Creates a trigger as the user. For a webhook, the response includes its secret
    /// path — the only time (besides the workspace chat) it is shown.</summary>
    [HttpPost("{id}/triggers")]
    public async Task<IActionResult> AddTrigger(string id, [FromBody] TriggerBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        if (!Enum.TryParse<TriggerKind>(body.Kind, ignoreCase: true, out var kind)) return BadRequest(new { error = "kind must be schedule or webhook" });

        var result = await Workspace(id).AddTrigger(new TriggerSpec
        {
            Kind = kind,
            Name = body.Name,
            Instruction = body.Instruction ?? string.Empty,
            TargetAgentId = body.TargetAgentId,
            EveryMinutes = body.EveryMinutes,
            Cron = body.Cron
        }, "user", idempotencyKey: string.Empty, revealSecret: true);

        return result.Success
            ? Content(result.ResultJson ?? "{}", "application/json")
            : BadRequest(new { error = result.Message });
    }

    [HttpDelete("{id}/triggers/{triggerId}")]
    public async Task<IActionResult> RemoveTrigger(string id, string triggerId)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        await Workspace(id).RemoveTrigger(triggerId, "user");
        return NoContent();
    }

    [HttpPut("{id}/budget")]
    public async Task<IActionResult> Budget(string id, [FromBody] BudgetBody body)
    {
        if (!WorkspaceIds.IsWorkspace(id)) return NotFound();
        await Workspace(id).UpdateBudget(body.DailyTokenLimit, body.DailyCostLimitUsd);
        return NoContent();
    }

    [HttpPost("{id}/pause")]
    public async Task<IActionResult> Pause(string id) { await Workspace(id).Pause(); return NoContent(); }

    [HttpPost("{id}/resume")]
    public async Task<IActionResult> Resume(string id) { await Workspace(id).Resume(); return NoContent(); }

    [HttpPost("{id}/archive")]
    public async Task<IActionResult> Archive(string id) { await Workspace(id).Archive(); return NoContent(); }
}

/// <summary>
/// Public inbound webhook endpoint for workspace triggers. Authenticated by the secret in the URL
/// (constant-time comparison); acknowledged with 202 only after the event is durably queued for the
/// agent, so a sender that gets 202 knows it was received. Redeliveries are dropped by delivery id.
/// </summary>
[ApiController]
[Route("api/hooks")]
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
