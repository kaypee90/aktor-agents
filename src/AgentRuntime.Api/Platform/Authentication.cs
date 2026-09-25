using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentRuntime.Infrastructure.Identity;
using AgentRuntime.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// Authenticates every request as a <see cref="Caller"/>: an API key (<c>Authorization: Bearer ak_…</c>)
/// or a browser session (an HttpOnly cookie). In <c>Auth:Mode=disabled</c> everyone is the owner of
/// the default organization.
/// </summary>
public sealed class AktorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IdentityService identity,
    IOptions<AuthOptions> authOptions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Aktor";
    private const string CallerKey = "aktor.caller";

    public static Caller? CallerOf(HttpContext http) => http.Items.TryGetValue(CallerKey, out var c) ? c as Caller : null;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Caller? caller;
        if (authOptions.Value.Disabled)
        {
            caller = new Caller(TenantIds.Default, TenantRole.Owner, "local", null, null, PlatformAdmin: true, SessionHash: null);
        }
        else if (Request.Headers.Authorization.ToString() is { Length: > 0 } header)
        {
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.Fail("Unsupported authorization scheme.");
            caller = await identity.ResolveApiKeyAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
            if (caller is null) return AuthenticateResult.Fail("Invalid or revoked API key.");
        }
        else if (Request.Cookies.TryGetValue(authOptions.Value.CookieName, out var token) && !string.IsNullOrEmpty(token))
        {
            caller = await identity.ResolveSessionAsync(token, Context.RequestAborted);
            if (caller is null) return AuthenticateResult.Fail("Session expired.");
        }
        else
        {
            return AuthenticateResult.NoResult();
        }

        Context.Items[CallerKey] = caller;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, caller.ActorId),
            new("tenant", caller.TenantId),
            new(ClaimTypes.Role, caller.Role.ToString()),
            new("auth", caller.IsApiKey ? "api_key" : authOptions.Value.Disabled ? "disabled" : "session")
        };
        if (caller.PlatformAdmin) claims.Add(new Claim("platform_admin", "true"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { error = "Sign in, or send an API key as 'Authorization: Bearer ak_…'." });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = "Your role in this organization doesn't allow that." });
    }
}

/// <summary>Role policies. Every endpoint needs at least a Viewer unless it's marked anonymous.</summary>
public static class Policies
{
    public const string Member = "member";
    public const string Admin = "admin";
    public const string Owner = "owner";
    public const string PlatformAdmin = "platform-admin";

    public static void Add(AuthorizationOptions options)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder(AktorAuthenticationHandler.SchemeName).RequireAuthenticatedUser().Build();
        options.AddPolicy(Member, p => AtLeast(p, TenantRole.Member));
        options.AddPolicy(Admin, p => AtLeast(p, TenantRole.Admin));
        options.AddPolicy(Owner, p => AtLeast(p, TenantRole.Owner));
        options.AddPolicy(PlatformAdmin, p => p.AddAuthenticationSchemes(AktorAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser().RequireClaim("platform_admin", "true"));
    }

    private static void AtLeast(AuthorizationPolicyBuilder policy, TenantRole minimum) =>
        policy.AddAuthenticationSchemes(AktorAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireAssertion(ctx =>
            Enum.TryParse<TenantRole>(ctx.User.FindFirstValue(ClaimTypes.Role), out var role) && role >= minimum);
}

public static class CallerHttpExtensions
{
    /// <summary>The authenticated caller. Only call from endpoints that require authentication.</summary>
    public static Caller Caller(this HttpContext http) =>
        AktorAuthenticationHandler.CallerOf(http) ?? throw new InvalidOperationException("The request isn't authenticated.");
}
