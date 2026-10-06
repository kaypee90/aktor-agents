using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Approval requests waiting for a person across the organization's workspaces, so the dashboard
/// can show them wherever you are (docs/safety.md). Deciding one stays per workspace:
/// POST /api/workspaces/{id}/approvals/{approvalId}/decision.
/// </summary>
[ApiController]
[Route("api/approvals")]
public sealed class ApprovalsController(IGrainFactory grains, AgentDbContext db, TenantAccess access) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        var workspaces = await db.Workspaces.AsNoTracking()
            .Where(w => w.TenantId == access.TenantId && w.Status != "Archived")
            .Select(w => new { w.WorkspaceId, w.Name })
            .ToListAsync(ct);

        var pending = await Task.WhenAll(workspaces.Select(async w => (w, approvals: await grains.GetGrain<IWorkspaceGrain>(w.WorkspaceId).GetPendingApprovals())));
        return Ok(pending
            .SelectMany(p => p.approvals.Select(a => new
            {
                workspace_id = p.w.WorkspaceId,
                workspace_name = p.w.Name,
                approval = a
            }))
            .OrderBy(x => x.approval.RequestedAt));
    }
}
