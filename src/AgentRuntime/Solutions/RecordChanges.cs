using AgentRuntime.Workspaces;

namespace AgentRuntime.Solutions;

/// <summary>One change to a solution record, as delivered to workspaces' record triggers and the
/// builder's webhooks. Written in the same transaction as the record, so none is lost.</summary>
[GenerateSerializer]
public sealed record RecordChange
{
    [Id(0)] public long ChangeId { get; init; }
    [Id(1)] public required string TenantId { get; init; }
    [Id(2)] public required string InstallId { get; init; }
    [Id(3)] public required string Entity { get; init; }
    [Id(4)] public required string RecordId { get; init; }
    /// <summary>"created", "updated" or "deleted".</summary>
    [Id(5)] public required string Event { get; init; }
    /// <summary>The record after the change (before it, for a delete).</summary>
    [Id(6)] public string DataJson { get; init; } = "{}";
    [Id(7)] public string? PreviousJson { get; init; }
    [Id(8)] public DateTimeOffset At { get; init; }
    /// <summary>Who changed it: an agent id, "api:…", or "enduser:…".</summary>
    [Id(9)] public string ChangedBy { get; init; } = string.Empty;
    /// <summary>The task or pipeline run whose agent or action made the change, if any.</summary>
    [Id(10)] public string? TaskId { get; init; }
}

public static class RecordEvents
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";

    public static readonly string[] All = [Created, Updated, Deleted];
}

/// <summary>Rules for <see cref="TriggerKind.Record"/> triggers.</summary>
public static class RecordTriggers
{
    public static List<string> Events(TriggerSpec spec) =>
        spec.On is { Count: > 0 } on ? on.Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList() : [RecordEvents.Created];

    public static string? Validate(TriggerSpec spec)
    {
        if (!RecordSchema.IsValidEntityName(spec.Entity?.Trim())) return "A record trigger needs the entity it watches (e.g. \"Ticket\").";
        if (Events(spec).FirstOrDefault(e => !RecordEvents.All.Contains(e)) is { } bad) return $"'{bad}' isn't a record change (use created, updated or deleted).";
        if (spec.Rule is { Conditions.Count: > 0 } rule && WatchEvaluator.Validate(rule with { ItemsPath = string.Empty }) is { } ruleError) return ruleError;
        return null;
    }

    public static bool Matches(TriggerDefinition trigger, RecordChange change)
    {
        if (!string.Equals(trigger.Entity, change.Entity, StringComparison.Ordinal)) return false;
        if (!trigger.RecordEvents.Contains(change.Event)) return false;
        if (trigger.Rule is not { Conditions.Count: > 0 } rule) return true;
        return WatchEvaluator.Evaluate(change.DataJson, rule with { ItemsPath = string.Empty }).Matches.Count > 0;
    }
}
