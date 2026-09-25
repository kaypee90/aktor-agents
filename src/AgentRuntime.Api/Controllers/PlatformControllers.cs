using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Billing;
using AgentRuntime.Infrastructure.Identity;
using AgentRuntime.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Controllers;

/// <summary>Accounts and sessions (docs/platform.md). The session lives in an HttpOnly cookie that
/// scripts can't read; API clients use API keys instead.</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(IdentityService identity, IOptions<AuthOptions> authOptions, BillingService billing) : ControllerBase
{
    public sealed record SignUpBody(string Email, string Password, string? Name, string? Organization, string? Invitation);
    public sealed record SignInBody(string Email, string Password);
    public sealed record SwitchBody(string TenantId);
    public sealed record PasswordBody(string CurrentPassword, string NewPassword);
    public sealed record AcceptBody(string Invitation);

    private AuthOptions Auth => authOptions.Value;

    [HttpPost("signup")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SignUp([FromBody] SignUpBody body, CancellationToken ct)
    {
        if (Auth.Disabled) return BadRequest(new { error = "Accounts are disabled on this server (Auth:Mode=disabled)." });
        try
        {
            var result = await identity.SignUpAsync(body.Email, body.Password, body.Name, body.Organization, body.Invitation, ct);
            if (!result.Success) return BadRequest(new { error = result.Error });
            SetCookie(result.Value!.SessionToken);
            return Ok(new { user_id = result.Value.User.UserId, tenant_id = result.Value.TenantId });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return BadRequest(new { error = "An account with this email already exists. Sign in instead." });
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] SignInBody body, CancellationToken ct)
    {
        if (Auth.Disabled) return BadRequest(new { error = "Accounts are disabled on this server (Auth:Mode=disabled)." });
        var result = await identity.SignInAsync(body.Email, body.Password, ct);
        if (!result.Success) return Unauthorized(new { error = result.Error });
        SetCookie(result.Value!.SessionToken);
        return Ok(new { user_id = result.Value.User.UserId, tenant_id = result.Value.TenantId });
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (AktorAuthenticationHandler.CallerOf(HttpContext) is { SessionHash: { } hash }) await identity.SignOutAsync(hash, ct);
        Response.Cookies.Delete(Auth.CookieName, CookieOptions());
        return NoContent();
    }

    /// <summary>Who's signed in, their organizations, and how this server is set up. Anonymous
    /// callers get the setup only, which is what the sign-in page needs.</summary>
    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var server = new
        {
            auth_mode = Auth.Disabled ? "disabled" : "accounts",
            signup_allowed = Auth.AllowSignup,
            billing_enabled = billing.Enabled
        };
        if (AktorAuthenticationHandler.CallerOf(HttpContext) is not { } caller)
        {
            return Ok(new { authenticated = false, server });
        }

        var memberships = caller.UserId is { } userId && !Auth.Disabled
            ? await identity.MembershipsAsync(userId, ct)
            : [new MembershipView(caller.TenantId, (await identity.GetTenantAsync(caller.TenantId, ct))?.Name ?? "Default", caller.Role)];
        return Ok(new
        {
            authenticated = true,
            server,
            user = new { user_id = caller.UserId, email = caller.Email, platform_admin = caller.PlatformAdmin },
            via = caller.IsApiKey ? "api_key" : Auth.Disabled ? "disabled" : "session",
            tenant_id = caller.TenantId,
            role = caller.Role.ToString(),
            organizations = memberships.Select(m => new { tenant_id = m.TenantId, name = m.TenantName, role = m.Role.ToString() })
        });
    }

    [HttpPost("switch")]
    public async Task<IActionResult> Switch([FromBody] SwitchBody body, CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        if (caller.SessionHash is null || caller.UserId is null) return BadRequest(new { error = "Only a signed-in browser can switch organizations." });
        return await identity.SwitchTenantAsync(caller.SessionHash, caller.UserId, body.TenantId, ct) ? NoContent() : NotFound();
    }

    [HttpPost("password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword([FromBody] PasswordBody body, CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        if (caller.UserId is null || caller.SessionHash is null) return BadRequest(new { error = "Sign in to change your password." });
        var result = await identity.ChangePasswordAsync(caller.UserId, body.CurrentPassword, body.NewPassword, caller.SessionHash, ct);
        return result.Success ? NoContent() : BadRequest(new { error = result.Error });
    }

    /// <summary>What an invitation link is for (for the sign-up page).</summary>
    [HttpGet("invitations/{token}")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> PeekInvitation(string token, CancellationToken ct) =>
        await identity.PeekInvitationAsync(token, ct) is { } i
            ? Ok(new { organization = i.TenantName, email = i.Email, role = i.Role.ToString() })
            : NotFound(new { error = "This invitation is invalid or has expired." });

    /// <summary>A signed-in user joining the organization they were invited to.</summary>
    [HttpPost("invitations/accept")]
    public async Task<IActionResult> Accept([FromBody] AcceptBody body, CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        if (caller.UserId is null || caller.SessionHash is null) return BadRequest(new { error = "Sign in to accept an invitation." });
        var result = await identity.AcceptInvitationAsync(caller.UserId, body.Invitation, ct);
        if (!result.Success) return BadRequest(new { error = result.Error });
        await identity.SwitchTenantAsync(caller.SessionHash, caller.UserId, result.Value!, ct);
        return Ok(new { tenant_id = result.Value });
    }

    private void SetCookie(string token) =>
        Response.Cookies.Append(Auth.CookieName, token, CookieOptions(DateTimeOffset.UtcNow.AddDays(Math.Max(1, Auth.SessionDays))));

    private CookieOptions CookieOptions(DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        // Lax: the cookie isn't sent on cross-site POSTs, which (with CORS) keeps other sites from
        // acting as the user.
        SameSite = SameSiteMode.Lax,
        Secure = Request.IsHttps,
        Path = "/",
        Expires = expires
    };
}

/// <summary>The caller's organization: its name, members and invitations.</summary>
[ApiController]
[Route("api/organization")]
public sealed class OrganizationController(IdentityService identity, IGrainFactory grains) : ControllerBase
{
    public sealed record RenameBody(string Name);
    public sealed record InviteBody(string Email, string Role);
    public sealed record RoleBody(string Role);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        var tenant = await identity.GetTenantAsync(caller.TenantId, ct);
        var usage = await grains.GetGrain<ITenantGrain>(caller.TenantId).GetUsage();
        return Ok(new
        {
            tenant_id = caller.TenantId,
            name = tenant?.Name ?? "Default",
            created_at = tenant?.CreatedAt,
            your_role = caller.Role.ToString(),
            plan = usage.Plan,
            members = (await identity.MembersAsync(caller.TenantId, ct)).Count
        });
    }

    [HttpPatch]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Rename([FromBody] RenameBody body, CancellationToken ct)
    {
        await identity.RenameTenantAsync(HttpContext.Caller().TenantId, body.Name, ct);
        return NoContent();
    }

    [HttpGet("members")]
    public async Task<IActionResult> Members(CancellationToken ct) =>
        Ok((await identity.MembersAsync(HttpContext.Caller().TenantId, ct)).Select(m => new
        {
            user_id = m.UserId, email = m.Email, name = m.Name, role = m.Role.ToString(), joined_at = m.JoinedAt
        }));

    [HttpPut("members/{userId}/role")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetRole(string userId, [FromBody] RoleBody body, CancellationToken ct)
    {
        if (!Enum.TryParse<TenantRole>(body.Role, ignoreCase: true, out var role)) return BadRequest(new { error = "role must be Viewer, Member, Admin or Owner" });
        var result = await identity.SetRoleAsync(HttpContext.Caller(), userId, role, ct);
        return result.Success ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpDelete("members/{userId}")]
    public async Task<IActionResult> Remove(string userId, CancellationToken ct)
    {
        var caller = HttpContext.Caller();
        // Anyone may leave; removing someone else takes an admin.
        if (caller.UserId != userId && caller.Role < TenantRole.Admin) return StatusCode(403, new { error = "Only admins can remove members." });
        var result = await identity.RemoveMemberAsync(caller, userId, ct);
        return result.Success ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpGet("invitations")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Invitations(CancellationToken ct) =>
        Ok((await identity.InvitationsAsync(HttpContext.Caller().TenantId, ct)).Select(i => new
        {
            invitation_id = i.InvitationId, email = i.Email, role = i.Role.ToString(), created_at = i.CreatedAt,
            expires_at = i.ExpiresAt, accepted_at = i.AcceptedAt, revoked_at = i.RevokedAt
        }));

    /// <summary>Returns the invitation token once; the dashboard turns it into a link to share.</summary>
    [HttpPost("invitations")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Invite([FromBody] InviteBody body, CancellationToken ct)
    {
        if (!Enum.TryParse<TenantRole>(body.Role, ignoreCase: true, out var role)) return BadRequest(new { error = "role must be Viewer, Member, Admin or Owner" });
        var result = await identity.InviteAsync(HttpContext.Caller(), body.Email, role, ct);
        if (!result.Success) return BadRequest(new { error = result.Error });
        var (invitation, token) = result.Value;
        return Ok(new { invitation_id = invitation.InvitationId, email = invitation.Email, role = invitation.Role.ToString(), expires_at = invitation.ExpiresAt, token });
    }

    [HttpDelete("invitations/{invitationId}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Revoke(string invitationId, CancellationToken ct) =>
        await identity.RevokeInvitationAsync(HttpContext.Caller().TenantId, invitationId, ct) ? NoContent() : NotFound();
}

/// <summary>API keys for the SDK and integrations. The secret is returned once, at creation.</summary>
[ApiController]
[Route("api/api-keys")]
[Authorize(Policies.Admin)]
public sealed class ApiKeysController(IdentityService identity) : ControllerBase
{
    public sealed record CreateBody(string Name, string? Role, int? ExpiresInDays);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await identity.ApiKeysAsync(HttpContext.Caller().TenantId, ct)).Select(k => new
        {
            key_id = k.KeyId, name = k.Name, display = k.Display, role = k.Role.ToString(), created_at = k.CreatedAt,
            last_used_at = k.LastUsedAt, expires_at = k.ExpiresAt, revoked_at = k.RevokedAt
        }));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBody body, CancellationToken ct)
    {
        var role = TenantRole.Member;
        if (body.Role is not null && !Enum.TryParse(body.Role, ignoreCase: true, out role)) return BadRequest(new { error = "role must be Viewer, Member or Admin" });
        var result = await identity.CreateApiKeyAsync(HttpContext.Caller(), body.Name, role, body.ExpiresInDays, ct);
        if (!result.Success) return BadRequest(new { error = result.Error });
        var (key, secret) = result.Value;
        return Ok(new { key_id = key.KeyId, name = key.Name, role = key.Role.ToString(), expires_at = key.ExpiresAt, key = secret });
    }

    [HttpDelete("{keyId}")]
    public async Task<IActionResult> Revoke(string keyId, CancellationToken ct) =>
        await identity.RevokeApiKeyAsync(HttpContext.Caller().TenantId, keyId, ct) ? NoContent() : NotFound();
}

/// <summary>The organization's plan, metered usage and (when a provider is configured) purchasing.</summary>
[ApiController]
[Route("api/billing")]
public sealed class BillingController(BillingService billing, IBillingProvider provider, IdentityService identity, IGrainFactory grains,
    ILogger<BillingController> logger) : ControllerBase
{
    public sealed record CheckoutBody(string PlanId);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var usage = await grains.GetGrain<ITenantGrain>(HttpContext.Caller().TenantId).GetUsage();
        return Ok(new
        {
            billing_enabled = billing.Enabled,
            plan = usage.Plan,
            subscription = new { status = usage.Billing.SubscriptionStatus, current_period_end = usage.Billing.CurrentPeriodEnd, has_billing_account = usage.Billing.CustomerId is not null },
            usage = usage.Current,
            history = usage.History,
            quota = usage.Quota,
            paused_agents = usage.ParkedAgents,
            plans = billing.PlansFor(usage.Plan.Id).Select(p => new { p.Id, p.Name, p.Description, p.MonthlyTokenLimit, p.MonthlyCostLimitUsd, p.MaxWorkspaces, p.MaxActiveAgents, p.MaxMembers, p.PriceMonthlyUsd, purchasable = !string.IsNullOrEmpty(p.StripePriceId) })
        });
    }

    [HttpPost("checkout")]
    [Authorize(Policies.Owner)]
    public async Task<IActionResult> Checkout([FromBody] CheckoutBody body, CancellationToken ct)
    {
        if (!provider.Enabled) return BadRequest(new { error = "Billing isn't enabled on this server; an operator sets plans." });
        var plan = billing.Plans.FirstOrDefault(p => p.Id == body.PlanId);
        if (plan is null) return BadRequest(new { error = "Unknown plan." });
        var caller = HttpContext.Caller();
        var tenant = await identity.GetTenantAsync(caller.TenantId, ct);
        if (tenant is null) return NotFound();
        try
        {
            return Ok(new { url = await provider.CreateCheckoutUrlAsync(tenant, plan, caller.Email, ct) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("portal")]
    [Authorize(Policies.Owner)]
    public async Task<IActionResult> Portal(CancellationToken ct)
    {
        if (!provider.Enabled) return BadRequest(new { error = "Billing isn't enabled on this server." });
        var tenant = await identity.GetTenantAsync(HttpContext.Caller().TenantId, ct);
        if (tenant is null) return NotFound();
        try
        {
            return Ok(new { url = await provider.CreatePortalUrlAsync(tenant, ct) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Stripe's webhook. Authenticated by its signature, not a session; each event is applied once.</summary>
    [HttpPost("stripe/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> StripeWebhook(CancellationToken ct)
    {
        const int maxBytes = 256 * 1024;
        if (Request.ContentLength > maxBytes) return StatusCode(413);
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        if (payload.Length > maxBytes) return StatusCode(413);

        var evt = provider.ParseWebhook(payload, Request.Headers["Stripe-Signature"]);
        if (evt is null)
        {
            logger.LogWarning("Rejected a billing webhook with a missing or invalid signature");
            return BadRequest(new { error = "invalid signature" });
        }

        await billing.HandleWebhookAsync(evt, ct);
        return Ok(new { received = true });
    }
}

/// <summary>Operator controls over organizations (self-hosted plans, support).</summary>
[ApiController]
[Route("api/admin/tenants")]
[Authorize(Policies.PlatformAdmin)]
public sealed class TenantAdminController(BillingService billing) : ControllerBase
{
    public sealed record PlanBody(string PlanId);

    [HttpPut("{tenantId}/plan")]
    public async Task<IActionResult> SetPlan(string tenantId, [FromBody] PlanBody body, CancellationToken ct) =>
        await billing.SetPlanAsync(tenantId, body.PlanId, ct) ? NoContent() : NotFound(new { error = "Unknown organization or plan." });
}
