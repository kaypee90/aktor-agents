using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Solutions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The end-user API (docs/solutions.md#end-users): what a solution's own customers reach, through
/// the embed widget or the builder's app, with an end-user token (<c>Authorization: Bearer aeu_…</c>)
/// the builder's backend issued. It's open to any origin (the token is the credential, no cookies),
/// and it can reach nothing but the token's app and user.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableCors(AppsCors.Policy)]
[Route("api/apps/{installId}")]
public sealed class AppsController(SolutionTokens tokens, SolutionAppService apps, EndUserJwtVerifier jwts, AgentRuntime.Solutions.ISolutionCatalog catalog)
    : ControllerBase
{
    /// <summary>An Aktor end-user token, or (for a solution that accepts them) the builder's own
    /// sign-in JWT, checked against the builder's published keys.</summary>
    private async Task<SolutionAppService.EndUser?> UserAsync(string installId, CancellationToken ct)
    {
        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var claims = tokens.VerifyEndUserToken(token);
        if (claims is null && EndUserJwtVerifier.LooksLikeJwt(token) && await catalog.FindAsync(installId, ct) is { Manifest.EndUsers.Jwt: not null } solution)
        {
            claims = await jwts.VerifyAsync(token!, solution, ct);
        }

        if (claims is null) return null;
        return await apps.ResolveAsync(claims, installId, Request.Headers.Origin.ToString(), ct);
    }

    private async Task<IActionResult> As(string installId, Func<SolutionAppService.EndUser, Task<object>> action, CancellationToken ct)
    {
        try
        {
            if (await UserAsync(installId, ct) is not { } user) return Unauthorized(new { error = "An end-user token is required (Authorization: Bearer aeu_…), and it may have expired." });
            return Ok(await action(user));
        }
        catch (SolutionException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    [HttpGet]
    public Task<IActionResult> Describe(string installId, CancellationToken ct) => As(installId, u => Task.FromResult(apps.Describe(u)), ct);

    public sealed record MessageBody(string Message);

    [HttpGet("conversations")]
    public Task<IActionResult> Conversations(string installId, CancellationToken ct) => As(installId, u => apps.ListAsync(u, ct), ct);

    /// <summary>Starts a conversation with the assistant; poll it for the reply.</summary>
    [HttpPost("conversations")]
    public Task<IActionResult> Start(string installId, [FromBody] MessageBody body, CancellationToken ct) =>
        As(installId, u => apps.StartAsync(u, body.Message, ct), ct);

    [HttpGet("conversations/{conversationId}")]
    public Task<IActionResult> Conversation(string installId, string conversationId, CancellationToken ct) =>
        As(installId, u => apps.GetAsync(u, conversationId, ct), ct);

    [HttpPost("conversations/{conversationId}/messages")]
    public Task<IActionResult> Send(string installId, string conversationId, [FromBody] MessageBody body, CancellationToken ct) =>
        As(installId, u => apps.SendAsync(u, conversationId, body.Message, ct), ct);

    /// <summary>The end user's own records of an entity that belongs to people.</summary>
    [HttpGet("records/{entity}")]
    public Task<IActionResult> Records(string installId, string entity, [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct) =>
        As(installId, u => apps.RecordsAsync(u, entity, limit ?? 50, offset ?? 0, ct), ct);
}

public static class AppsCors
{
    public const string Policy = "apps";
}
