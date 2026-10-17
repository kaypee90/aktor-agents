using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// A plugin that runs out of process, in any language (docs/plugins.md#remote-plugins): an HTTP
/// service that answers five POSTs — <c>/validate</c>, <c>/tools/list</c>, <c>/tools/call</c>,
/// <c>/notify</c> and <c>/inbound</c> — with JSON. It can provide tools, deliver notifications and
/// parse inbound messages, like a built-in plugin, but its code never runs inside the server: the
/// runtime calls it, with a shared secret, and applies the same limits, approvals, audit and
/// idempotency to it as to any connection. The Python SDK has a server for it (sdk/python).
/// </summary>
public sealed class RemotePlugin(IHttpClientFactory httpFactory) : IToolProviderPlugin, INotificationChannelPlugin, IInboundChannelPlugin
{
    public const string PluginId = "remote-plugin";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Remote plugin",
        Description = "A plugin you run yourself, in any language, as an HTTP service: its tools, notifications and inbound messages work like a built-in plugin's.",
        Category = PluginCategory.Other,
        Settings =
        [
            new PluginSettingField { Key = "base_url", Label = "Plugin URL", Required = true, Placeholder = "https://plugins.example.com/crm", Description = "Where the plugin service listens; the runtime POSTs to /validate, /tools/list, /tools/call, /notify and /inbound under it." },
            new PluginSettingField { Key = "config", Label = "Configuration (JSON)", Description = "Passed to the plugin with every call, e.g. which account or region to use." },
            new PluginSettingField { Key = "token", Label = "Shared secret", Secret = true, Required = true, Description = "Sent as 'Authorization: Bearer …' on every call, so the plugin can check the call is from this server." },
            new PluginSettingField { Key = "credentials", Label = "Credentials (JSON)", Secret = true, Description = "Secrets the plugin needs (an API key for the system it talks to), sent with each call and never shown to agents." }
        ],
        SetupHelp = "Run a plugin service (see docs/plugins.md#remote-plugins, or sdk/python's aktor.plugin), then give its URL and the shared secret it expects."
    };

    private async Task<JsonNode?> PostAsync(PluginConnection connection, string path, JsonObject body, CancellationToken ct)
    {
        var baseUrl = connection.Setting("base_url").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("The plugin URL must be an absolute http(s) URL.");
        }

        body["connection"] = new JsonObject
        {
            ["id"] = connection.ConnectionId,
            ["name"] = connection.Name,
            ["workspace_id"] = connection.WorkspaceId,
            ["config"] = ParseConfig(connection.Setting("config"))
        };
        if (connection.Secrets.TryGetValue("credentials", out var credentials) && !string.IsNullOrWhiteSpace(credentials))
        {
            body["credentials"] = ParseConfig(credentials);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        {
            Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Secret("token"));
        request.Headers.Add("X-Aktor-Plugin-Protocol", "1");
        using var response = await httpFactory.CreateClient("integrations").SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryParse(text)?["error"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
            throw new HttpRequestException($"The plugin answered {path}: {message}");
        }

        return TryParse(text) ?? throw new InvalidOperationException($"The plugin's {path} answer isn't JSON.");
    }

    private static JsonNode? ParseConfig(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return TryParse(text) ?? JsonValue.Create(text);
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken ct)
    {
        try
        {
            var answer = await PostAsync(connection, "/validate", [], ct);
            var ok = answer?["ok"]?.GetValue<bool>() ?? false;
            var message = answer?["message"]?.GetValue<string>() ?? (ok ? "Connected." : "The plugin refused the connection.");
            return ok ? ConnectionCheck.Success(message) : ConnectionCheck.Failure(message);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return ConnectionCheck.Failure($"Couldn't reach the plugin: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken ct)
    {
        var answer = await PostAsync(connection, "/tools/list", [], ct);
        var tools = new List<ToolDefinition>();
        foreach (var t in answer?["tools"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (t["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            tools.Add(new ToolDefinition
            {
                Name = name,
                Description = t["description"]?.GetValue<string>() ?? name,
                JsonSchema = (t["input_schema"] as JsonObject)?.ToJsonString() ?? """{ "type": "object", "properties": {} }""",
                // Undeclared side effects are treated as the unsafe kind: never re-run blindly.
                SideEffects = t["side_effects"]?.GetValue<string>()?.ToLowerInvariant() switch
                {
                    "read_only" or "readonly" => ToolSideEffects.ReadOnly,
                    "idempotent" => ToolSideEffects.Idempotent,
                    _ => ToolSideEffects.NonIdempotent
                }
            });
        }

        return tools;
    }

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        JsonNode? arguments;
        try
        {
            arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
        }
        catch (JsonException)
        {
            return ToolExecutionResult.Fail("The arguments aren't JSON.");
        }

        var answer = await PostAsync(connection, "/tools/call", new JsonObject
        {
            ["tool"] = toolName,
            ["arguments"] = arguments,
            // Pass it to the system the plugin talks to, so a repeated call can't take effect twice.
            ["idempotency_key"] = request.IdempotencyKey,
            ["agent_id"] = request.AgentId,
            ["task_id"] = request.TaskId
        }, request.CancellationToken);

        if (answer?["ok"]?.GetValue<bool>() == false)
        {
            return ToolExecutionResult.Fail(answer["error"]?.GetValue<string>() ?? "The plugin's tool failed.", answer["code"]?.GetValue<string>() ?? "plugin_error");
        }

        var result = answer?["result"];
        return ToolExecutionResult.Ok(result is null ? "{}" : result is JsonValue v && v.TryGetValue<string>(out var s) ? JsonSerializer.Serialize(new { text = s }) : result.ToJsonString());
    }

    public async Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification notification, CancellationToken ct)
    {
        try
        {
            var answer = await PostAsync(connection, "/notify", new JsonObject
            {
                ["delivery_id"] = notification.DeliveryId,
                ["workspace"] = notification.WorkspaceName,
                ["author"] = notification.AuthorName,
                ["text"] = notification.Text,
                ["urgency"] = notification.Urgency
            }, ct);
            return answer?["delivered"]?.GetValue<bool>() == true
                ? DeliveryResult.Ok()
                : DeliveryResult.Failed(answer?["error"]?.GetValue<string>() ?? "The plugin didn't deliver it.", answer?["retryable"]?.GetValue<bool>() ?? true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return DeliveryResult.Failed(ex.Message);
        }
    }

    public async Task<InboundResult> ParseInboundAsync(PluginConnection connection, InboundRequest request, CancellationToken ct)
    {
        var answer = await PostAsync(connection, "/inbound", new JsonObject
        {
            ["body"] = request.Body,
            ["content_type"] = request.ContentType,
            ["headers"] = new JsonObject(request.Headers.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)JsonValue.Create(h.Value)))),
            ["query"] = new JsonObject(request.Query.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)JsonValue.Create(h.Value)))),
            ["public_url"] = request.PublicUrl
        }, ct);
        if (answer?["rejected"]?.GetValue<bool>() == true) return InboundResult.Reject();
        var responseBody = answer?["response_body"]?.GetValue<string>() ?? string.Empty;
        var contentType = answer?["response_content_type"]?.GetValue<string>() ?? "text/plain";
        if (answer?["is_message"]?.GetValue<bool>() != true) return InboundResult.Ignore(responseBody, contentType);
        return new InboundResult
        {
            IsMessage = true,
            SenderId = answer["sender_id"]?.GetValue<string>(),
            SenderName = answer["sender_name"]?.GetValue<string>(),
            Text = answer["text"]?.GetValue<string>(),
            MessageId = answer["message_id"]?.GetValue<string>(),
            ResponseBody = responseBody,
            ResponseContentType = contentType
        };
    }
}
