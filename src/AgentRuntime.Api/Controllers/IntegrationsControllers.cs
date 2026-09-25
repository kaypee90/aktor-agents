using AgentRuntime.Integrations;
using AgentRuntime.Plugins;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>The installed plugins (built-in and from the plugins folder), for the "add connection" UI.</summary>
[ApiController]
[Route("api/plugins")]
public sealed class PluginsController(PluginCatalog catalog) : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(catalog.All
        .OrderBy(p => p.Manifest.Category).ThenBy(p => p.Manifest.Name)
        .Select(p => new
        {
            id = p.Manifest.Id,
            name = p.Manifest.Name,
            description = p.Manifest.Description,
            version = p.Manifest.Version,
            category = p.Manifest.Category.ToString(),
            setup_help = p.Manifest.SetupHelp,
            provides_tools = p is IToolProviderPlugin,
            supports_notifications = p is INotificationChannelPlugin,
            supports_inbound = p is IInboundChannelPlugin,
            settings = p.Manifest.Settings.Select(s => new
            {
                key = s.Key,
                label = s.Label,
                description = s.Description,
                secret = s.Secret,
                required = s.Required,
                placeholder = s.Placeholder,
                default_value = s.DefaultValue,
                options = s.Options
            })
        }));
}

/// <summary>A workspace's connections. Secrets are accepted here and go straight to the
/// encrypted vault; no endpoint ever returns them.</summary>
[ApiController]
[Route("api/workspaces/{workspaceId}/connections")]
[AgentRuntime.Api.Platform.WorkspaceAccess]
public sealed class ConnectionsController(IGrainFactory grains) : ControllerBase
{
    public sealed record AddBody(string PluginId, string Name, Dictionary<string, string>? Settings, Dictionary<string, string>? Secrets,
        string? NotifyLevel, List<string>? AllowedSenders);
    public sealed record UpdateBody(string? NotifyLevel, List<string>? EnabledTools, List<string>? AllowedSenders);

    private IWorkspaceGrain Workspace(string id) => grains.GetGrain<IWorkspaceGrain>(id);

    [HttpGet]
    public async Task<IActionResult> List(string workspaceId) =>
        WorkspaceIds.IsWorkspace(workspaceId) ? Ok(await Workspace(workspaceId).ListConnections()) : NotFound();

    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> Add(string workspaceId, [FromBody] AddBody body)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId)) return NotFound();
        NotifyLevel? level = null;
        if (body.NotifyLevel is not null)
        {
            if (!Enum.TryParse<NotifyLevel>(body.NotifyLevel, ignoreCase: true, out var parsed)) return BadRequest(new { error = "notify_level must be off, urgent, warning or all" });
            level = parsed;
        }

        var result = await Workspace(workspaceId).AddConnection(new ConnectionRequest
        {
            PluginId = body.PluginId,
            Name = body.Name,
            Settings = body.Settings ?? [],
            Secrets = body.Secrets ?? [],
            NotifyLevel = level,
            AllowedSenders = body.AllowedSenders ?? []
        });
        return result.Success ? Ok(new { message = result.Message, connection = result.Connection }) : BadRequest(new { error = result.Message });
    }

    [HttpPatch("{connectionId}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> Update(string workspaceId, string connectionId, [FromBody] UpdateBody body)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId)) return NotFound();
        NotifyLevel? level = null;
        if (body.NotifyLevel is not null && Enum.TryParse<NotifyLevel>(body.NotifyLevel, ignoreCase: true, out var parsed)) level = parsed;

        var result = await Workspace(workspaceId).UpdateConnection(connectionId, new ConnectionUpdate
        {
            NotifyLevel = level,
            EnabledTools = body.EnabledTools,
            AllowedSenders = body.AllowedSenders
        });
        return result.Success ? Ok(result.Connection) : BadRequest(new { error = result.Message });
    }

    [HttpPost("{connectionId}/refresh")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> Refresh(string workspaceId, string connectionId)
    {
        var result = await Workspace(workspaceId).RefreshConnectionTools(connectionId);
        return result.Success ? Ok(result.Connection) : BadRequest(new { error = result.Message });
    }

    [HttpDelete("{connectionId}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AgentRuntime.Api.Platform.Policies.Admin)]
    public async Task<IActionResult> Remove(string workspaceId, string connectionId)
    {
        await Workspace(workspaceId).RemoveConnection(connectionId);
        return NoContent();
    }
}

/// <summary>
/// Public inbound endpoint for messaging connections (Twilio SMS replies, Telegram messages).
/// Authenticated by the per-connection secret in the URL; the plugin then verifies the
/// provider's own signature, and only allowed senders reach the agents.
/// </summary>
[ApiController]
[Route("api/channels")]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class ChannelsController(IGrainFactory grains) : ControllerBase
{
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly string[] ForwardedHeaders = ["X-Twilio-Signature", "X-Telegram-Bot-Api-Secret-Token", "User-Agent"];

    [HttpPost("{workspaceId}/{connectionId}/{token}")]
    public async Task<IActionResult> Receive(string workspaceId, string connectionId, string token)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId)) return NotFound();
        if (Request.ContentLength > MaxBodyBytes) return StatusCode(413);

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > MaxBodyBytes) return StatusCode(413);
            body = new string(buffer, 0, read);
        }

        var response = await grains.GetGrain<IWorkspaceGrain>(workspaceId).HandleInbound(connectionId, token, new InboundRequestDto
        {
            Body = body,
            ContentType = Request.ContentType,
            Headers = ForwardedHeaders.Where(h => Request.Headers.ContainsKey(h)).ToDictionary(h => h, h => Request.Headers[h].ToString()),
            Query = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString())
        });

        return response.StatusCode == 200
            ? Content(response.Body, response.ContentType)
            : StatusCode(response.StatusCode);
    }
}
