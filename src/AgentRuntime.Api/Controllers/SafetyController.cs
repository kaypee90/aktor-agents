using AgentRuntime.Api.Platform;
using AgentRuntime.Safety;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>A workspace's safety policy, its approval requests and its audit log (docs/safety.md).</summary>
[ApiController]
[Route("api/workspaces/{workspaceId}")]
[AgentRuntime.Api.Platform.WorkspaceAccess]
public sealed class SafetyController(IGrainFactory grains, IAuditLog audit) : ControllerBase
{
    public sealed record DecisionBody(bool Approve, string? Reason);

    private IWorkspaceGrain Workspace(string id) => grains.GetGrain<IWorkspaceGrain>(id);

    /// <summary>Who decided, for the chat and the audit log: the person's email, or the API key.</summary>
    private string Who() => HttpContext.Caller() is var c && c.Email is { } email ? email : c.ActorId == "local" ? "user" : c.ActorId;

    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(string workspaceId) =>
        WorkspaceIds.IsWorkspace(workspaceId) ? Ok(await Workspace(workspaceId).GetSafetyPolicy()) : NotFound();

    [HttpPut("policy")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> PutPolicy(string workspaceId, [FromBody] WorkspaceSafetyPolicy policy)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId) || await Workspace(workspaceId).GetSnapshot() is null) return NotFound();
        if (policy.Rules.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.ToolPattern)) is not null)
        {
            return BadRequest(new { error = "Every rule needs a tool pattern (use * for any tool)." });
        }

        await Workspace(workspaceId).UpdateSafetyPolicy(policy, Who());
        return Ok(await Workspace(workspaceId).GetSafetyPolicy());
    }

    [HttpGet("approvals")]
    public async Task<IActionResult> Approvals(string workspaceId, [FromQuery] string? status)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId) || await Workspace(workspaceId).GetSnapshot() is not { } snapshot) return NotFound();
        IEnumerable<ApprovalRecord> list = snapshot.Approvals;
        if (status is not null)
        {
            if (!Enum.TryParse<ApprovalStatus>(status, ignoreCase: true, out var s)) return BadRequest(new { error = "status must be Pending, Approved, Rejected or Expired" });
            list = list.Where(a => a.Status == s);
        }

        return Ok(list.OrderByDescending(a => a.RequestedAt));
    }

    [HttpPost("approvals/{approvalId}/decision")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Member)]
    public async Task<IActionResult> Decide(string workspaceId, string approvalId, [FromBody] DecisionBody body)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId)) return NotFound();
        var result = await Workspace(workspaceId).DecideApproval(approvalId, body.Approve, body.Reason, Who(), HttpContext.Caller().IsApiKey ? "api" : "dashboard");
        return result.Success ? Ok(new { message = result.Message }) : BadRequest(new { error = result.Message });
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(string workspaceId, [FromQuery] string? actor, [FromQuery] string? action,
        [FromQuery] string? q, [FromQuery] DateTimeOffset? since, [FromQuery] long? before, [FromQuery] int limit = 100)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId)) return NotFound();
        return Ok(await audit.QueryAsync(new AuditQuery
        {
            Scope = workspaceId,
            ActorId = actor,
            ActionPrefix = action,
            Text = q,
            Since = since,
            BeforeSeq = before,
            Limit = Math.Clamp(limit, 1, 500)
        }, HttpContext.RequestAborted));
    }

    [HttpGet("audit/verify")]
    public async Task<IActionResult> Verify(string workspaceId) =>
        WorkspaceIds.IsWorkspace(workspaceId) ? Ok(await audit.VerifyAsync(workspaceId, HttpContext.RequestAborted)) : NotFound();
}
