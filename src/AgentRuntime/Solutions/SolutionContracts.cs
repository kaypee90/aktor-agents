using System.Text.Json.Nodes;

namespace AgentRuntime.Solutions;

/// <summary>
/// A solution: a business application built on the platform (docs/solutions.md), packaged as one
/// unit that installs into an organization, upgrades in place and uninstalls. It bundles the
/// workspaces (pipelines, triggers, safety policies, budgets), the data model its agents work on,
/// the connectors it needs, its skills and knowledge, the assistant its own customers talk to, and
/// its evals. Authored as <c>aktor.yaml</c> plus files; the server reads it as JSON (snake_case).
/// </summary>
public sealed record SolutionManifest
{
    /// <summary>The manifest format; only 1 exists.</summary>
    public int Format { get; init; } = 1;
    /// <summary>Stable id within an organization: lowercase letters, digits and dashes.</summary>
    public string Name { get; init; } = string.Empty;
    public string? Title { get; init; }
    /// <summary>Semantic version, e.g. "1.2.0".</summary>
    public string Version { get; init; } = string.Empty;
    public string? Description { get; init; }
    /// <summary>The platform versions the solution works on, e.g. ">=1.0.0" (see
    /// <see cref="SolutionValidator.PlatformVersion"/>); null: any.</summary>
    public string? RequiresPlatform { get; init; }
    /// <summary>Values asked for at install (a fee, a sender number): workspaces see them as
    /// <c>{{ vars.key }}</c>; connector settings may use <c>{{ inputs.key }}</c>.</summary>
    public List<SolutionInput> Inputs { get; init; } = [];
    /// <summary>Integrations the solution needs. Their secrets are never in the package: they're
    /// asked for at install and kept in the vault.</summary>
    public List<SolutionConnector> Connectors { get; init; } = [];
    /// <summary>The solution's data model, by entity name (e.g. "Lease").</summary>
    public Dictionary<string, EntityDefinition> Entities { get; init; } = [];
    public List<SolutionWorkspace> Workspaces { get; init; } = [];
    public List<SolutionSkill> Skills { get; init; } = [];
    public List<SolutionKnowledge> Knowledge { get; init; } = [];
    /// <summary>The agent the solution's own customers (end users) talk to, if any.</summary>
    public SolutionAssistant? Assistant { get; init; }
    /// <summary>How end users sign in, besides tokens the server issues (docs/solutions.md#your-own-sign-in).</summary>
    public SolutionEndUsers? EndUsers { get; init; }
    /// <summary>Records created at first install only (demo or reference data), by entity.</summary>
    public Dictionary<string, List<JsonObject>> Seed { get; init; } = [];
    /// <summary>The builder's backend, told about record changes (signed POSTs).</summary>
    public List<SolutionWebhook> Webhooks { get; init; } = [];
    /// <summary>Eval cases (docs/solutions.md#evals), run by <c>aktor test</c>.</summary>
    public List<SolutionEval> Evals { get; init; } = [];

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Name : Title!;
}

public sealed record SolutionInput
{
    public string Key { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    /// <summary>string, text, number, integer or boolean.</summary>
    public string Type { get; init; } = "string";
    public string? Default { get; init; }
    public bool Required { get; init; }
    public List<string>? Options { get; init; }
}

public sealed record SolutionConnector
{
    /// <summary>The connection's name in each workspace that uses it (its tool prefix).</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>The plugin id, e.g. "twilio-sms" or "mcp".</summary>
    public string Plugin { get; init; } = string.Empty;
    public string? Description { get; init; }
    /// <summary>Non-secret settings; may use <c>{{ inputs.key }}</c>.</summary>
    public Dictionary<string, string> Settings { get; init; } = [];
    /// <summary>When false, the solution installs without it and its tools are just missing.</summary>
    public bool Required { get; init; } = true;
}

/// <summary>An entity type in the solution's data model.</summary>
public sealed record EntityDefinition
{
    public string? Description { get; init; }
    public Dictionary<string, FieldDefinition> Fields { get; init; } = [];
    /// <summary>The field shown as the record's title (default: the first string field).</summary>
    public string? TitleField { get; init; }
}

public sealed record FieldDefinition
{
    /// <summary>See <see cref="FieldTypes"/>.</summary>
    public string Type { get; init; } = FieldTypes.String;
    public bool Required { get; init; }
    public string? Description { get; init; }
    /// <summary>Allowed values of an <c>enum</c> field.</summary>
    public List<string>? Values { get; init; }
    /// <summary>The entity a <c>ref</c> field points at.</summary>
    public string? Entity { get; init; }
    /// <summary>Set on create when the field is missing.</summary>
    public JsonNode? Default { get; init; }
    /// <summary>Keep a database index on the field, for filtering and sorting large tables by it.</summary>
    public bool Indexed { get; init; }
}

public static class FieldTypes
{
    public const string String = "string";
    public const string Text = "text";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Enum = "enum";
    /// <summary>A record id of another entity.</summary>
    public const string Ref = "ref";
    /// <summary>The end user a record belongs to: set by the runtime, never by the agent or the
    /// end user, and the only records an end user's conversation can see.</summary>
    public const string EndUser = "end_user";
    public const string Json = "json";

    public static readonly string[] All = [String, Text, Integer, Number, Boolean, Date, DateTime, Enum, Ref, EndUser, Json];
}

/// <summary>One workspace of the solution.</summary>
public sealed record SolutionWorkspace
{
    /// <summary>Stable key across versions (what an upgrade matches workspaces by).</summary>
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Goal { get; init; } = string.Empty;
    public Pipelines.PipelineDefinition? Pipeline { get; init; }
    public List<Workspaces.TriggerSpec> Triggers { get; init; } = [];
    public Safety.WorkspaceSafetyPolicy? Safety { get; init; }
    public int? DailyTokens { get; init; }
    public decimal? DailyCostUsd { get; init; }
    /// <summary>Names of <see cref="SolutionManifest.Connectors"/> this workspace uses.</summary>
    public List<string> Connectors { get; init; } = [];
    /// <summary>What its agents may do with each entity: "read" or "write".</summary>
    public Dictionary<string, string> Data { get; init; } = [];
    /// <summary>Extra workspace variables (inputs are added automatically).</summary>
    public Dictionary<string, string> Variables { get; init; } = [];
}

/// <summary>A skill in the Agent Skills format (a SKILL.md in the package).</summary>
public sealed record SolutionSkill
{
    public string Path { get; init; } = string.Empty;
    /// <summary>A workspace key to make it that workspace's only; null: every workspace of the solution.</summary>
    public string? Workspace { get; init; }
}

/// <summary>Reference knowledge agents search (a policy, a price list, a tone of voice).</summary>
public sealed record SolutionKnowledge
{
    public string Key { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string? Text { get; init; }
    public string? Workspace { get; init; }
}

/// <summary>The agent end users talk to (through the embed widget or the apps API).</summary>
public sealed record SolutionAssistant
{
    public string Name { get; init; } = "Assistant";
    public string Instructions { get; init; } = string.Empty;
    public string? Greeting { get; init; }
    /// <summary>Entity access in an end user's conversation; records with an <c>end_user</c> field
    /// are limited to that end user's own.</summary>
    public Dictionary<string, string> Data { get; init; } = [];
    public List<string> Connectors { get; init; } = [];
    public int MaxTokens { get; init; } = 30_000;
    public decimal MaxCostUsd { get; init; } = 0.50m;
    /// <summary>Conversations one end user may start per day.</summary>
    public int MaxConversationsPerDay { get; init; } = 20;
    /// <summary>Messages one end user may send per minute.</summary>
    public int MaxMessagesPerMinute { get; init; } = 10;
    /// <summary>Sites (origins like "https://app.example.com") whose pages may call the apps API
    /// for this solution; empty: any (the end-user token is the credential either way).</summary>
    public List<string> AllowedOrigins { get; init; } = [];
    /// <summary>Most one end user's conversations may cost in a calendar month (UTC), in US
    /// dollars; null: no cap beyond each message's budget. Builders set it to what their plan pays for.</summary>
    public decimal? MonthlyCostLimitUsdPerUser { get; init; }
}

public sealed record SolutionEndUsers
{
    /// <summary>Accept the builder's own sign-in tokens (JWTs) on the apps API, checked against
    /// their published keys, instead of exchanging them for an Aktor token first.</summary>
    public SolutionJwt? Jwt { get; init; }
}

/// <summary>Which JWTs identify an end user. Values may use <c>{{ inputs.key }}</c>.</summary>
public sealed record SolutionJwt
{
    /// <summary>The <c>iss</c> the tokens must have, e.g. "https://auth.example.com/".</summary>
    public string Issuer { get; init; } = string.Empty;
    /// <summary>The <c>aud</c> they must be for.</summary>
    public string Audience { get; init; } = string.Empty;
    /// <summary>Where the issuer publishes its signing keys (a JWKS document, https).</summary>
    public string JwksUrl { get; init; } = string.Empty;
    /// <summary>The claim that holds your id for the user.</summary>
    public string UserClaim { get; init; } = "sub";
    /// <summary>The claim with their display name, if any.</summary>
    public string? NameClaim { get; init; } = "name";
}

public sealed record SolutionWebhook
{
    public string Url { get; init; } = string.Empty;
    /// <summary>record.created, record.updated, record.deleted (or "record.*").</summary>
    public List<string> Events { get; init; } = ["record.*"];
    /// <summary>Only these entities; empty: all.</summary>
    public List<string> Entities { get; init; } = [];
}

/// <summary>An expected behaviour, checked by running the workspace for real.</summary>
public sealed record SolutionEval
{
    public string Name { get; init; } = string.Empty;
    public string Workspace { get; init; } = string.Empty;
    public string Input { get; init; } = string.Empty;
    public JsonNode? InputData { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    public SolutionEvalExpectations Expect { get; init; } = new();
}

public sealed record SolutionEvalExpectations
{
    /// <summary>The run's final status, e.g. "Completed" (the default).</summary>
    public string? Status { get; init; } = "Completed";
    /// <summary>Phrases the run's summary must contain (case-insensitive).</summary>
    public List<string> SummaryContains { get; init; } = [];
    public List<string> SummaryNotContains { get; init; } = [];
    /// <summary>Record counts created or changed during the run, by entity.</summary>
    public Dictionary<string, RecordExpectation> Records { get; init; } = [];
}

public sealed record RecordExpectation
{
    public int? AtLeast { get; init; }
    public int? AtMost { get; init; }
    /// <summary>Field values the counted records must have.</summary>
    public Dictionary<string, string> Where { get; init; } = [];
}

/// <summary>A solution package as uploaded: the manifest's text (YAML or JSON) and the files it
/// refers to (skills, knowledge, workspace files, evals), by relative path.</summary>
public sealed record SolutionBundle
{
    public string Manifest { get; init; } = string.Empty;
    public Dictionary<string, string> Files { get; init; } = [];
}

public enum SolutionInstallStatus
{
    Active,
    Uninstalled,
    /// <summary>Being installed: its records connection works, but record changes wait.</summary>
    Installing
}

/// <summary>Who may do what with an entity.</summary>
public enum EntityAccess
{
    None,
    Read,
    Write
}

public static class EntityAccessNames
{
    public static EntityAccess Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "read" => EntityAccess.Read,
        "write" => EntityAccess.Write,
        _ => EntityAccess.None
    };

    public static bool IsValid(string? value) => value?.Trim().ToLowerInvariant() is "read" or "write";
}

public static class SolutionIds
{
    public const string InstallPrefix = "sol-";
    public const string RecordPrefix = "rec-";

    public static string NewInstall() => InstallPrefix + Guid.NewGuid().ToString("n")[..10];

    public static string NewRecord() => RecordPrefix + Guid.NewGuid().ToString("n")[..12];

    /// <summary>The same record id for the same tool call, so a create replayed after a crash
    /// finds the record it made instead of making a second.</summary>
    public static string RecordFor(string idempotencyKey) => Contracts.DeterministicId.From(RecordPrefix, idempotencyKey);

    /// <summary>"enduser:{install}:{user}": who started an end user's conversation.</summary>
    public static string EndUserActor(string installId, string userId) => $"enduser:{installId}:{userId}";
}
