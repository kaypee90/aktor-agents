using AgentRuntime.Api.Platform;
using AgentRuntime.Solutions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The solution registry (docs/solutions.md#the-registry): publish versions of your packages,
/// find packages other organizations made public, and install one by name with
/// <c>POST /api/v1/solutions { "package": "name@version" }</c>.
/// </summary>
[ApiController]
[Route("api/registry/packages")]
public sealed class RegistryController(SolutionRegistryService registry) : ControllerBase
{
    private IActionResult Problem(SolutionException ex) => StatusCode(ex.Status, new { error = ex.Message, errors = ex.Errors });

    public sealed record PublishBody(SolutionBundle Bundle, string? Visibility);

    /// <summary>Publishes a version (Admins). Visibility "public" lists it for every organization on
    /// this server; "organization" (the default) for yours only.</summary>
    [HttpPost]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Publish([FromBody] PublishBody body, CancellationToken ct)
    {
        try
        {
            return StatusCode(201, await registry.PublishAsync(HttpContext.Caller().TenantId, HttpContext.Caller().ActorId, body.Bundle, body.Visibility ?? "organization", ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct) => Ok(await registry.SearchAsync(HttpContext.Caller().TenantId, q, ct));

    [HttpGet("{name}")]
    public async Task<IActionResult> Versions(string name, CancellationToken ct)
    {
        try
        {
            return Ok(await registry.VersionsAsync(HttpContext.Caller().TenantId, name, ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>A version's package (its manifest and files), integrity-checked: to read, or to
    /// validate before installing. "latest" for the newest version that isn't yanked.</summary>
    [HttpGet("{name}/{version}")]
    public async Task<IActionResult> Get(string name, string version, CancellationToken ct)
    {
        try
        {
            var (bundle, package) = await registry.ResolveAsync(HttpContext.Caller().TenantId, $"{name}@{version}", ct);
            return Ok(new { package = package.PackageId, version = package.Version, sha256 = package.Sha256, yanked = package.Yanked, bundle });
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    public sealed record YankBody(bool Yank = true, string? Reason = null);

    /// <summary>Yanks a version you published (installs by "latest" skip it; existing installs keep working), or un-yanks it.</summary>
    [HttpPost("{name}/{version}/yank")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Yank(string name, string version, [FromBody] YankBody? body, CancellationToken ct)
    {
        try
        {
            await registry.YankAsync(HttpContext.Caller().TenantId, name, version, body?.Yank ?? true, body?.Reason, ct);
            return NoContent();
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }
}
