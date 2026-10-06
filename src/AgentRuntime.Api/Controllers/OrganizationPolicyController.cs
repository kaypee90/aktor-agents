using System.Text.Json;
using AgentRuntime.Api.Platform;
using AgentRuntime.Safety;
using AgentRuntime.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The organization's safety policy (docs/safety.md#organization-policy): rules, a minimum autonomy
/// and team-shape limits set once by an admin and applied to every workspace and task, on top of
/// each workspace's own policy. Changes are recorded in the organization's audit log.
/// </summary>
[ApiController]
[Route("api/organization/policy")]
public sealed class OrganizationPolicyController(IGrainFactory grains, IAuditLog audit, TenantAccess access) : ControllerBase
{
    private ITenantGrain Tenant => grains.GetGrain<ITenantGrain>(TenantIds.Normalize(access.TenantId));

    /// <summary>The organization's audit scope (a workspace's scope is its id).</summary>
    public static string AuditScope(string tenantId) => $"org-{TenantIds.Normalize(tenantId)}";

    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await Tenant.GetSafetyPolicy());

    [HttpPut]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Admin)]
    public async Task<IActionResult> Put([FromBody] OrganizationSafetyPolicy policy, CancellationToken ct)
    {
        if (policy.Rules.Any(r => string.IsNullOrWhiteSpace(r.ToolPattern)))
            return BadRequest(new { error = "Every rule needs a tool pattern (use * for any tool)." });
        if (policy.Rules.Count > 50) return BadRequest(new { error = "At most 50 rules." });

        var before = await Tenant.GetSafetyPolicy();
        var after = await Tenant.SetSafetyPolicy(policy, Who());
        try
        {
            await audit.AppendAsync(new AuditEntry
            {
                Scope = AuditScope(access.TenantId),
                Key = $"org-policy:{Guid.NewGuid():n}",
                ActorType = "user",
                ActorId = Who(),
                ActorName = Who(),
                Action = "policy.updated",
                Target = "organization safety policy",
                Summary = $"Minimum {before.MinimumAutonomy} → {after.MinimumAutonomy}, {after.Rules.Count} rule(s)" +
                          (after.Team is { IsEmpty: false } ? ", team-shape rules set" : ""),
                DetailJson = JsonSerializer.Serialize(new { before, after })
            }, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The policy is saved and enforced either way; a failed audit write is logged by the store.
        }

        return Ok(after);
    }

    /// <summary>Changes to the organization's policy, newest first.</summary>
    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await audit.QueryAsync(new AuditQuery { Scope = AuditScope(access.TenantId), Limit = Math.Clamp(limit, 1, 200) }, ct));

    private string Who() => HttpContext.Caller() is var c && c.Email is { } email ? email : c.ActorId == "local" ? "user" : c.ActorId;
}
