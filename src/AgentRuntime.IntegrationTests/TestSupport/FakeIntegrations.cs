using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Integrations;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>Stand-in for the encrypted vault: process-wide, so it survives silo kills like a database.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    public static readonly ConcurrentDictionary<(string Scope, string Key), string> Values = new();

    public Task PutAsync(string scope, string key, string value, CancellationToken ct = default)
    {
        Values[(scope, key)] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string scope, string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue((scope, key), out var v) ? v : null);

    public Task DeleteScopeAsync(string scope, CancellationToken ct = default)
    {
        foreach (var k in Values.Keys.Where(k => k.Scope == scope)) Values.TryRemove(k, out _);
        return Task.CompletedTask;
    }
}

/// <summary>A test plugin covering all three plugin roles: a CRM lookup tool, a notification
/// channel (which can be told to fail), and inbound messages ("sender|message-id|text").</summary>
public sealed class FakeCrmPlugin : IToolProviderPlugin, INotificationChannelPlugin, IInboundChannelPlugin
{
    public static readonly ConcurrentQueue<(string Tool, string Args, string ApiKey)> ToolCalls = new();
    public static readonly ConcurrentQueue<OutboundNotification> Notifications = new();
    public static int FailNextNotifications;
    /// <summary>What list_inventory returns (sku -> quantity); tests change it between checks.</summary>
    public static readonly ConcurrentDictionary<string, int> Inventory = new();

    public static void Reset()
    {
        ToolCalls.Clear();
        Notifications.Clear();
        FailNextNotifications = 0;
        Inventory.Clear();
    }

    public PluginManifest Manifest { get; } = new()
    {
        Id = "fake-crm",
        Name = "Fake CRM",
        Description = "Test plugin.",
        Settings =
        [
            new() { Key = "region", Label = "Region", Required = true },
            new() { Key = "api_key", Label = "API key", Secret = true, Required = true }
        ]
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken ct) =>
        Task.FromResult(connection.Secrets.GetValueOrDefault("api_key") == "bad" ? ConnectionCheck.Failure("Invalid API key.") : ConnectionCheck.Success());

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>([
            new()
            {
                Name = "lookup_customer",
                Description = "Look up a customer by id.",
                SideEffects = ToolSideEffects.ReadOnly,
                JsonSchema = """{ "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }"""
            },
            new()
            {
                Name = "list_inventory",
                Description = "List stock levels.",
                SideEffects = ToolSideEffects.ReadOnly,
                JsonSchema = """{ "type": "object", "properties": {} }"""
            },
            new()
            {
                Name = "delete_customer",
                Description = "Delete a customer.",
                SideEffects = ToolSideEffects.NonIdempotent,
                JsonSchema = """{ "type": "object", "properties": { "id": { "type": "string" } } }"""
            }
        ]);

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        ToolCalls.Enqueue((toolName, request.ArgumentsJson, connection.Secret("api_key")));
        if (toolName == "list_inventory")
        {
            // Shaped like an HTTP API result: the body is a JSON string.
            var body = JsonSerializer.Serialize(new { items = Inventory.OrderBy(kv => kv.Key).Select(kv => new { sku = kv.Key, qty = kv.Value }) });
            return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new { status = 200, body })));
        }

        return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new { customer = "Ama Mensah", tier = "gold", region = connection.Setting("region") })));
    }

    public Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification notification, CancellationToken ct)
    {
        if (Interlocked.Decrement(ref FailNextNotifications) >= 0) return Task.FromResult(DeliveryResult.Failed("temporarily down"));
        Notifications.Enqueue(notification);
        return Task.FromResult(DeliveryResult.Ok());
    }

    public Task<InboundResult> ParseInboundAsync(PluginConnection connection, InboundRequest request, CancellationToken ct)
    {
        var parts = request.Body.Split('|', 3);
        return Task.FromResult(parts.Length == 3
            ? new InboundResult { IsMessage = true, SenderId = parts[0], MessageId = parts[1], Text = parts[2], ResponseBody = "ok" }
            : InboundResult.Ignore());
    }
}

public static class TestAudit
{
    public static readonly AgentRuntime.Safety.InMemoryAuditLog Log = new();
}

/// <summary>Script stages in tests: the code names a handler here, which gets the stage's data.</summary>
public sealed class FakeScriptRunner : AgentRuntime.Pipelines.IScriptRunner
{
    public static readonly ConcurrentDictionary<string, Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonNode?>> Handlers = new();
    public static readonly ConcurrentQueue<string> Seen = new();

    public Task<AgentRuntime.Pipelines.ScriptRunResult> RunAsync(AgentRuntime.Pipelines.ScriptRunRequest request, CancellationToken cancellationToken = default)
    {
        Seen.Enqueue(request.DataJson);
        if (!Handlers.TryGetValue(request.Code.Trim(), out var handler))
        {
            return Task.FromResult(AgentRuntime.Pipelines.ScriptRunResult.Failed($"NameError: no test script '{request.Code.Trim()}'"));
        }

        try
        {
            var data = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(request.DataJson)!;
            var output = handler(data);
            return Task.FromResult(new AgentRuntime.Pipelines.ScriptRunResult { Ok = true, OutputJson = output?.ToJsonString() ?? "null", Logs = $"ran {request.Code.Trim()}" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(AgentRuntime.Pipelines.ScriptRunResult.Failed($"{ex.GetType().Name}: {ex.Message}"));
        }
    }
}
