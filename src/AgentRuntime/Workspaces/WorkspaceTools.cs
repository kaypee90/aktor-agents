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

/// <summary>A recurring check that costs no LLM tokens: the runtime calls a read-only tool on a
/// schedule and evaluates the conditions itself, alerting (or waking an agent) only on new matches.</summary>
public sealed class CreateWatchTool(IGrainFactory grains) : WorkspaceToolBase(grains)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "create_watch",
        Description = "Set up a recurring check that runs WITHOUT you (no tokens per check): on a schedule the runtime calls a " +
                      "read-only connection tool, finds the items at items_path, and tests the conditions. Only items that newly " +
                      "match are reported — straight to the user (mode 'notify') or to an agent (mode 'wake_agent') when judgement " +
                      "is needed. Prefer this over create_schedule whenever the check is a clear condition (status == 'failed', " +
                      "amount > 1000, days_open >= 3, a count below a threshold). The response includes a dry run: check items_found " +
                      "and matching_now.",
        RequiredPermissions = ToolPermission.WorkspaceActions,
        SideEffects = ToolSideEffects.Idempotent,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "source_tool": { "type": "string", "description": "A read-only connection tool, e.g. crm__get" },
            "source_arguments": { "type": "object", "description": "Arguments for source_tool, e.g. { path: '/orders' }" },
            "items_path": { "type": "string", "description": "JSONPath to the list of items, e.g. $.body.data[*] (JSON inside strings is parsed)" },
            "conditions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "field": { "type": "string", "description": "Path within an item, e.g. status or totals.amount" },
                  "op": { "type": "string", "enum": ["<", "<=", ">", ">=", "==", "!=", "contains", "not_contains", "exists", "not_exists"] },
                  "value": { "type": "string" }
                },
                "required": ["field", "op"]
              }
            },
            "key_field": { "type": "string", "description": "Identifies an item across checks, e.g. id" },
            "display_fields": { "type": "array", "items": { "type": "string" }, "description": "Shown per matching item, e.g. id and status" },
            "every_minutes": { "type": "number" },
            "cron": { "type": "string" },
            "mode": { "type": "string", "enum": ["notify", "wake_agent"] },
            "message": { "type": "string", "description": "Alert text for mode 'notify'; placeholders {count}, {items}, {name}" },
            "urgency": { "type": "string", "enum": ["info", "warning", "urgent"] },
            "instruction": { "type": "string", "description": "For mode 'wake_agent': what the agent should do with the matches" },
            "target_agent_id": { "type": "string", "description": "For mode 'wake_agent'; defaults to you" }
          },
          "required": ["name", "source_tool", "conditions"]
        }
        """
    };

    protected override Task<WorkspaceActionResult> RunAsync(IWorkspaceGrain workspace, JsonElement args, ToolExecutionRequest request)
    {
        var conditions = args.TryGetProperty("conditions", out var cs) && cs.ValueKind == JsonValueKind.Array
            ? cs.EnumerateArray().Select(c => new WatchCondition
            {
                Field = Str(c, "field") ?? string.Empty,
                Op = Str(c, "op") ?? string.Empty,
                Value = Str(c, "value")
            }).ToList()
            : [];
        var display = args.TryGetProperty("display_fields", out var ds) && ds.ValueKind == JsonValueKind.Array
            ? ds.EnumerateArray().Select(d => d.ToString()).ToList()
            : [];

        return workspace.AddTrigger(new TriggerSpec
        {
            Kind = TriggerKind.Watch,
            Name = Str(args, "name") ?? "watch",
            Instruction = Str(args, "instruction") ?? string.Empty,
            EveryMinutes = Num(args, "every_minutes"),
            Cron = Str(args, "cron"),
            TargetAgentId = Str(args, "target_agent_id"),
            SourceTool = Str(args, "source_tool"),
            SourceArgumentsJson = args.TryGetProperty("source_arguments", out var sa) && sa.ValueKind == JsonValueKind.Object ? sa.GetRawText() : "{}",
            Rule = new WatchRule
            {
                ItemsPath = Str(args, "items_path") ?? string.Empty,
                Conditions = conditions,
                KeyField = Str(args, "key_field"),
                DisplayFields = display
            },
            WatchMode = Str(args, "mode"),
            MessageTemplate = Str(args, "message"),
            Urgency = Str(args, "urgency")
        }, request.AgentId, request.IdempotencyKey, revealSecret: false);
    }
}

public sealed class CreateWebhookTool(IGrainFactory grains) : WorkspaceToolBase(grains)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "create_webhook",
        Description = "Create an inbound webhook that wakes an agent (yourself by default) whenever an external service " +
                      "(a payment provider, a form, a code repository, your own app) posts to it. The secret URL is shown to the user, who connects it " +
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
        ["notify_user", "create_schedule", "create_watch", "create_webhook", "list_triggers", "delete_trigger", "wait_for_events"];

    public static IServiceCollection AddWorkspaceTools(this IServiceCollection services)
    {
        services.AddSingleton<ITool>(sp => new NotifyUserTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new CreateScheduleTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new CreateWebhookTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new CreateWatchTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new ListTriggersTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool>(sp => new DeleteTriggerTool(sp.GetRequiredService<IGrainFactory>()));
        services.AddSingleton<ITool, WaitForEventsTool>();
        return services;
    }
}
