using AgentRuntime.Api.Platform;
using AgentRuntime.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Global connectors (docs/plugins.md#global-connectors): integrations set up once for the
/// organization and linked from workspaces, tasks and solutions. Admins manage them; members see
/// them (never their secrets) so they can pick one. Secrets are accepted here and go straight to
/// the vault; no endpoint returns them.
/// </summary>
[ApiController]
[Route("api/connectors")]
public sealed class GlobalConnectorsController(GlobalConnectorService connectors, PluginCatalog plugins, TenantAccess access) : ControllerBase
{
    public sealed record Body(string? Name, string? PluginId, string? Description, Dictionary<string, string>? Settings,
        Dictionary<string, string>? Secrets, bool? UsableInTasks);

    private string By => HttpContext.Caller().Email ?? HttpContext.Caller().UserId ?? "api key";

    private IActionResult Problem(GlobalConnectorException ex) => StatusCode(ex.Status, new { error = ex.Message });

    private object View(GlobalConnector c, IReadOnlyList<GlobalConnectorUse>? uses) => new
    {
        connector_id = c.ConnectorId,
        name = c.Name,
        plugin_id = c.PluginId,
        plugin_name = plugins.Get(c.PluginId)?.Manifest.Name ?? c.PluginId,
        description = c.Description,
        settings = c.Settings,
        secret_keys = c.SecretKeys,
        usable_in_tasks = c.UsableInTasks,
        created_by = c.CreatedBy,
        created_at = c.CreatedAt,
        updated_at = c.UpdatedAt,
        used_by = (uses ?? []).Select(u => new { owner_kind = u.OwnerKind, owner_id = u.OwnerId, owner_name = u.OwnerName, connection_id = u.ConnectionId, connection_name = u.ConnectionName })
    };

    private static GlobalConnectorInput Input(Body body) => new()
    {
        Name = body.Name,
        PluginId = body.PluginId,
        Description = body.Description,
        Settings = body.Settings,
        Secrets = body.Secrets,
        UsableInTasks = body.UsableInTasks
    };

    [HttpGet]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var uses = await connectors.UsesAsync(access.TenantId, await access.HiddenWorkspacesAsync(ct), ct);
        return Ok((await connectors.ListAsync(access.TenantId, ct)).Select(c => View(c, uses.GetValueOrDefault(c.ConnectorId))));
    }

    [HttpGet("{id}")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        try
        {
            var c = await connectors.GetAsync(access.TenantId, id, ct);
            var uses = await connectors.UsesAsync(access.TenantId, await access.HiddenWorkspacesAsync(ct), ct);
            return Ok(View(c, uses.GetValueOrDefault(c.ConnectorId)));
        }
        catch (GlobalConnectorException ex)
        {
            return Problem(ex);
        }
    }

    [HttpPost]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Create([FromBody] Body body, CancellationToken ct)
    {
        try
        {
            var c = await connectors.CreateAsync(access.TenantId, By, Input(body), ct);
            return Ok(View(c, []));
        }
        catch (GlobalConnectorException ex)
        {
            return Problem(ex);
        }
    }

    [HttpPatch("{id}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Update(string id, [FromBody] Body body, CancellationToken ct)
    {
        try
        {
            var (c, refreshed, problems) = await connectors.UpdateAsync(access.TenantId, id, Input(body), ct);
            return Ok(new { connector = View(c, null), refreshed_connections = refreshed, problems });
        }
        catch (GlobalConnectorException ex)
        {
            return Problem(ex);
        }
    }

    [HttpPost("{id}/test")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Test(string id, CancellationToken ct)
    {
        try
        {
            return Ok(new { ok = true, message = await connectors.TestAsync(access.TenantId, id, ct) });
        }
        catch (GlobalConnectorException ex) when (ex.Status == StatusCodes.Status400BadRequest)
        {
            return Ok(new { ok = false, message = ex.Message });
        }
        catch (GlobalConnectorException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Deletes it and its secrets; <c>?force=true</c> also removes the connections that link it.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Delete(string id, [FromQuery] bool force, CancellationToken ct)
    {
        try
        {
            var removed = await connectors.DeleteAsync(access.TenantId, id, force, ct);
            return Ok(new { deleted = true, removed_connections = removed });
        }
        catch (GlobalConnectorException ex)
        {
            return Problem(ex);
        }
    }
}
