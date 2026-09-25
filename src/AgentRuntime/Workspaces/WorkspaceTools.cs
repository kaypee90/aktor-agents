using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRuntime.Workspaces;

/// <summary>
/// Tools for agents that live in a workspace. Each one only translates arguments; the workspace
/// grain validates, deduplicates by idempotency key, and applies the change.
/// </summary>
public abstract class WorkspaceToolBase(IGrainFactory grainFactory) : ITool
{
    public abstract ToolDefinition Definition { get; }

    protected abstract Task<WorkspaceActionResult> RunAsync(IWorkspaceGrain workspace, JsonElement args, ToolExecutionRequest request);

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        // A workspace agent's task id is its workspace id.
        if (!WorkspaceIds.IsWorkspace(request.TaskId))
        {
            return ToolExecutionResult.Fail($"'{Definition.Name}' only works inside a workspace.");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
        var result = await RunAsync(grainFactory.GetGrain<IWorkspaceGrain>(request.TaskId), doc.RootElement, request);
        return result.Success
            ? ToolExecutionResult.Ok(result.ResultJson ?? JsonSerializer.Serialize(new { ok = true, message = result.Message }, ToolJson.Options))
            : ToolExecutionResult.Fail(result.Message);
    }

    protected static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    protected static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}

public sealed class NotifyUserTool(IGrainFactory grains) : WorkspaceToolBase(grains)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "notify_user",
        Description = "Send a message to the user who owns this workspace (shown in their workspace chat; urgent " +
                      "messages may also be forwarded to their phone/email once connectors are set up). Use it for " +
                      "results, alerts and questions — not for routine 'nothing happened' updates.",
        RequiredPermissions = ToolPermission.WorkspaceActions,
        SideEffects = ToolSideEffects.Idempotent,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "text": { "type": "string" },
            "urgency": { "type": "string", "enum": ["info", "warning", "urgent"] }
          },
          "required": ["text"]
        }
        """
    };

    protected override Task<WorkspaceActionResult> RunAsync(IWorkspaceGrain workspace, JsonElement args, ToolExecutionRequest request) =>
        workspace.Notify(request.AgentId, Str(args, "text") ?? string.Empty, Str(args, "urgency") ?? "info", request.IdempotencyKey);
}

public sealed class CreateScheduleTool(IGrainFactory grains) : WorkspaceToolBase(grains)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "create_schedule",
        Description = "Wake an agent (yourself by default) on a schedule, with an instruction for what to do each time. " +
                      "Use every_minutes for intervals or cron (5 fields, UTC) for times of day. Prefer the longest " +
                      "interval that meets the need: every wake-up costs tokens.",
        RequiredPermissions = ToolPermission.WorkspaceActions,
        SideEffects = ToolSideEffects.Idempotent,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "instruction": { "type": "string", "description": "What the agent should do each time it fires" },
            "every_minutes": { "type": "number" },
            "cron": { "type": "string", "description": "e.g. '0 9 * * 1-5' for 09:00 UTC on weekdays" },
            "target_agent_id": { "type": "string", "description": "Defaults to you" }
          },
          "required": ["name", "instruction"]
        }
        """
    };

    protected override Task<WorkspaceActionResult> RunAsync(IWorkspaceGrain workspace, JsonElement args, ToolExecutionRequest request) =>
        workspace.AddTrigger(new TriggerSpec
        {
            Kind = TriggerKind.Schedule,
            Name = Str(args, "name") ?? "schedule",
            Instruction = Str(args, "instruction") ?? string.Empty,
            EveryMinutes = Num(args, "every_minutes"),
            Cron = Str(args, "cron"),
            TargetAgentId = Str(args, "target_agent_id")
        }, request.AgentId, request.IdempotencyKey, revealSecret: false);
}

public sealed class CreateWebhookTool(IGrainFactory grains) : WorkspaceToolBase(grains)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "create_webhook",
        Description = "Create an inbound webhook that wakes an agent (yourself by default) whenever an external service " +
                      "(e.g. Shopify, Stripe, GitHub) posts to it. The secret URL is shown to the user, who connects it " +
                      "to their service; you never see it. Prefer webhooks over polling schedules when a service supports them.",
        RequiredPermissions = ToolPermission.WorkspaceActions,
        SideEffects = ToolSideEffects.Idempotent,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "instruction": { "type": "string", "description": "What the agent should do with each delivery" },
            "target_agent_id": { "type": "string", "description": "Defaults to you" }
          },
          "required": ["name", "instruction"]
        }
        """
    };

    protected override Task<WorkspaceActionResult> RunAsync(IWorkspaceGrain workspace, JsonElement args, ToolExecutionRequest request) =>
        workspace.AddTrigger(new TriggerSpec
        {
            Kind = TriggerKind.Webhook,
            Name = Str(args, "name") ?? "webhook",
            Instruction = Str(args, "instruction") ?? string.Empty,
            TargetAgentId = Str(args, "target_agent_id")
        }, request.AgentId, request.IdempotencyKey, revealSecret: false);
}

public sealed class ListTriggersTool(IGrainFactory grains) : WorkspaceToolBase(grains)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "list_triggers",
        Description = "List this workspace's schedules and webhooks (without secrets).",
        RequiredPermissions = ToolPermission.WorkspaceActions,
        SideEffects = ToolSideEffects.ReadOnly,
        JsonSchema = """{ "type": "object", "properties": {} }"""
    };

    protected override async Task<WorkspaceActionResult> RunAsync(IWorkspaceGrain workspace, JsonElement args, ToolExecutionRequest request)
    {
        var triggers = await workspace.ListTriggers();
        return WorkspaceActionResult.Ok("ok", JsonSerializer.Serialize(new
        {
            triggers = triggers.Select(t => new
            {
                trigger_id = t.TriggerId,
                kind = t.Kind.ToString(),
                name = t.Name,
                target_agent_id = t.TargetAgentId,
                instruction = t.Instruction,
                every_seconds = t.IntervalSeconds,
                cron = t.Cron,
                fire_count = t.FireCount,
                last_fired_at = t.LastFiredAt
            })
        }, ToolJson.Options));
    }
}

public sealed class DeleteTriggerTool(IGrainFactory grains) : WorkspaceToolBase(grains)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "delete_trigger",
        Description = "Delete a schedule or webhook that is no longer needed.",
        RequiredPermissions = ToolPermission.WorkspaceActions,
        SideEffects = ToolSideEffects.Idempotent,
        JsonSchema = """{ "type": "object", "properties": { "trigger_id": { "type": "string" } }, "required": ["trigger_id"] }"""
    };

    protected override Task<WorkspaceActionResult> RunAsync(IWorkspaceGrain workspace, JsonElement args, ToolExecutionRequest request) =>
        workspace.RemoveTrigger(Str(args, "trigger_id") ?? string.Empty, request.AgentId);
}

/// <summary>Ends a standing agent's turn. The runtime handles it (like end_turn for residents);
/// the tool itself only acknowledges.</summary>
public sealed class WaitForEventsTool : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "wait_for_events",
        Description = "Finish your current work and go idle until you're woken by a message, schedule or webhook. " +
                      "Give a one-line summary of your current state.",
        RequiredPermissions = ToolPermission.WorkspaceActions,
        SideEffects = ToolSideEffects.ReadOnly,
        JsonSchema = """{ "type": "object", "properties": { "summary": { "type": "string" } }, "required": ["summary"] }"""
    };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) =>
        Task.FromResult(ToolExecutionResult.Ok("""{"ok":true,"message":"Waiting for the next message, schedule or webhook."}"""));
}

public static class WorkspaceToolCatalog
{
    public static readonly string[] ToolNames =
        ["notify_user", "create_schedule", "create_webhook", "list_triggers", "delete_trigger", "wait_for_events"];

    public static IServiceCollection AddWorkspaceTools(this IServiceCollection services)
    {
        services.AddSingleton<ITool>(sp => new NotifyUserTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new CreateScheduleTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new CreateWebhookTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new ListTriggersTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new DeleteTriggerTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool, WaitForEventsTool>();
        return services;
    }
}
