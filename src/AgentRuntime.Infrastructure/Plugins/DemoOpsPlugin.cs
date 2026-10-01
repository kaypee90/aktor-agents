using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// A simulated production system for the incident-response template (roadmap P9): logs, metrics and
/// recent deploys for one scripted incident, plus a rollback. The tools have the names and shapes
/// you'd give your real observability and deploy connections (an HTTP API plugin over Datadog or
/// Loki, GitHub deployments, your CD system), so the template works the same once you swap them in.
///
/// <para>The scripted incident: checkout-service v2.14.0 shipped a discount refactor; a minute later
/// error rates jump to ~18% with NullReferenceExceptions in the discount code. Rolling back to
/// v2.13.2 clears it: after a rollback, logs and metrics come back healthy.</para>
/// </summary>
public sealed class DemoOpsPlugin : IToolProviderPlugin
{
    /// <summary>Rollbacks per connection (the simulated system's state). Exposed for tests and demos.</summary>
    public static readonly ConcurrentDictionary<string, List<string>> Rollbacks = new();

    public PluginManifest Manifest { get; } = new()
    {
        Id = "demo-ops",
        Name = "Demo production system",
        Description = "A simulated service with logs, metrics, deploys and rollbacks, for trying the incident-response template.",
        Category = PluginCategory.Tools,
        SetupHelp = "No settings needed. Replace it with your real observability and deploy connections, keeping the tool names " +
                    "(query_logs, query_metrics, list_deploys, rollback_deploy) or adjusting the workspace's instructions."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(ConnectionCheck.Success());

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>(
        [
            new()
            {
                Name = "query_logs",
                Description = "Recent log lines for a service, newest first.",
                SideEffects = ToolSideEffects.ReadOnly,
                JsonSchema = """{ "type": "object", "properties": { "service": { "type": "string" }, "level": { "type": "string", "enum": ["error", "warn", "info"] }, "since_minutes": { "type": "integer" } }, "required": ["service"] }"""
            },
            new()
            {
                Name = "query_metrics",
                Description = "A metric for a service over recent minutes: error_rate (%), latency_p95_ms or requests_per_second.",
                SideEffects = ToolSideEffects.ReadOnly,
                JsonSchema = """{ "type": "object", "properties": { "service": { "type": "string" }, "metric": { "type": "string", "enum": ["error_rate", "latency_p95_ms", "requests_per_second"] }, "since_minutes": { "type": "integer" } }, "required": ["service", "metric"] }"""
            },
            new()
            {
                Name = "list_deploys",
                Description = "Recent deploys, newest first: version, time, author and change summary.",
                SideEffects = ToolSideEffects.ReadOnly,
                JsonSchema = """{ "type": "object", "properties": { "service": { "type": "string" }, "since_hours": { "type": "integer" } } }"""
            },
            new()
            {
                Name = "rollback_deploy",
                Description = "Roll a service back to an earlier version. Changes production: needs approval under SemiAutonomous mode.",
                // Not safe to repeat blindly: a second rollback could undo a fix shipped in between.
                SideEffects = ToolSideEffects.NonIdempotent,
                JsonSchema = """{ "type": "object", "properties": { "service": { "type": "string" }, "to_version": { "type": "string" }, "reason": { "type": "string" } }, "required": ["service", "to_version", "reason"] }"""
            }
        ]);

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
        var args = doc.RootElement;
        string Arg(string name, string fallback) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        var service = Arg("service", "checkout-service");
        var rolledBack = Rollbacks.TryGetValue(connection.ConnectionId, out var done) && done.Count > 0;
        var now = DateTimeOffset.UtcNow;
        var deployAt = now.AddMinutes(-22);

        object result = toolName switch
        {
            "query_logs" when service != "checkout-service" => new { service, lines = new[] { $"{now:u} INFO {service} healthy" } },
            "query_logs" => new
            {
                service,
                lines = rolledBack
                    ? new[] { $"{now:u} INFO checkout-service v2.13.2 serving normally after rollback" }
                    : new[]
                    {
                        $"{now.AddMinutes(-1):u} ERROR checkout-service v2.14.0 POST /checkout 500 NullReferenceException at DiscountService.ApplyLoyaltyDiscount (DiscountService.cs:88): customer.LoyaltyTier was null",
                        $"{now.AddMinutes(-2):u} ERROR checkout-service v2.14.0 POST /checkout 500 NullReferenceException at DiscountService.ApplyLoyaltyDiscount (DiscountService.cs:88)",
                        $"{now.AddMinutes(-3):u} WARN  checkout-service v2.14.0 retrying payment intent after 500 from discount step",
                        $"{deployAt.AddMinutes(1):u} ERROR checkout-service v2.14.0 first NullReferenceException at DiscountService.ApplyLoyaltyDiscount",
                        $"{deployAt:u} INFO  checkout-service v2.14.0 started (deploy #4812)"
                    },
                summary = rolledBack ? "No errors since the rollback." : "1,342 errors in the last 20 minutes, all NullReferenceException in DiscountService.ApplyLoyaltyDiscount, starting one minute after deploy #4812."
            },
            "query_metrics" => Arg("metric", "error_rate") switch
            {
                "latency_p95_ms" => new { service, metric = "latency_p95_ms", points = rolledBack ? Series(now, 210, 210) : Series(now, 210, 1450), note = rolledBack ? "normal" : "p95 up 7x since 22 minutes ago" },
                "requests_per_second" => new { service, metric = "requests_per_second", points = Series(now, 340, 335), note = "traffic is normal: not a load problem" },
                _ => new { service, metric = "error_rate", points = rolledBack ? Series(now, 0.2, 0.2) : Series(now, 0.2, 18.4), note = rolledBack ? "back to baseline" : "error rate 18.4% (baseline 0.2%), rising sharply 21 minutes ago" }
            },
            "list_deploys" => new
            {
                deploys = new object[]
                {
                    new { service = "checkout-service", version = "v2.14.0", deploy_id = 4812, at = deployAt, author = "ci (merge of PR #977)", change = "Refactor loyalty discounts into DiscountService" },
                    new { service = "search-service", version = "v5.2.1", deploy_id = 4809, at = now.AddHours(-3), author = "ci", change = "Index tuning" },
                    new { service = "checkout-service", version = "v2.13.2", deploy_id = 4790, at = now.AddDays(-2), author = "ci", change = "Payment retry fix" }
                }
            },
            "rollback_deploy" => Rollback(connection, service, Arg("to_version", "v2.13.2"), Arg("reason", string.Empty), request.IdempotencyKey),
            _ => new { error = $"Unknown tool {toolName}" }
        };

        return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(result)));
    }

    private static object Rollback(PluginConnection connection, string service, string version, string reason, string idempotencyKey)
    {
        var log = Rollbacks.GetOrAdd(connection.ConnectionId, _ => []);
        lock (log) log.Add($"{service}->{version}|{idempotencyKey}|{reason}");
        return new { rolled_back = true, service, to_version = version, deploy_id = 4813, message = $"{service} is now running {version}." };
    }

    private static object[] Series(DateTimeOffset now, double before, double after) =>
        Enumerable.Range(0, 6).Select(i =>
        {
            var at = now.AddMinutes(-30 + i * 6);
            return (object)new { at, value = at < now.AddMinutes(-21) ? before : after };
        }).ToArray();
}
