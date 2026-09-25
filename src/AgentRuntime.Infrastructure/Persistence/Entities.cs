namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Durable history tables (CLAUDE.md section 39). Orleans grain state stays optimized for active
/// execution; these rows are the permanent record, written by <see cref="PersistenceEventSubscriber"/>.
/// </summary>
public sealed class TaskRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public required string TaskId { get; set; }
    public required string Goal { get; set; }
    public string Status { get; set; } = "Running";
    public string? RootAgentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultSummary { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class AgentRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public required string AgentId { get; set; }
    public string? ParentAgentId { get; set; }
    public required string RootAgentId { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public required string Name { get; set; }
    public required string Role { get; set; }
    public required string Goal { get; set; }
    public string Status { get; set; } = "Created";
    public string CapabilitiesJson { get; set; } = "[]";
    public string AllowedToolsJson { get; set; } = "[]";
    public int Depth { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TokensUsed { get; set; }
    public int ToolCallsUsed { get; set; }
    public int ChildrenSpawned { get; set; }
    public decimal CostUsd { get; set; }
    public string? FailureReason { get; set; }
}

public sealed class MessageRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public required string MessageId { get; set; }
    public required string FromAgentId { get; set; }
    public required string ToAgentId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public required string MessageType { get; set; }
    public string Priority { get; set; } = "Normal";
    public DateTimeOffset Timestamp { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
}

public sealed class EventRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public long Id { get; set; }
    public required string EventId { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? AgentId { get; set; }
    public string? ParentAgentId { get; set; }
    public string? TargetAgentId { get; set; }
    public string? TaskId { get; set; }
    public string? CorrelationId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
}

public sealed class ArtifactRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public required string ArtifactId { get; set; }
    public required string Type { get; set; }
    public required string Location { get; set; }
    public required string CreatedByAgent { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string MetadataJson { get; set; } = "{}";
}

public sealed class MemoryEntity
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public required string MemoryId { get; set; }
    public required string AgentId { get; set; }
    public required string Kind { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ToolCallRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public long Id { get; set; }
    public required string AgentId { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public required string ToolName { get; set; }
    public string ArgumentsJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public bool Success { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>One simulated world. <see cref="SnapshotJson"/> is the latest full snapshot, so a world
/// stays inspectable after the in-memory world grain is gone (e.g. after a restart).</summary>
public sealed class WorldRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public required string WorldId { get; set; }
    public required string Name { get; set; }
    public string Seed { get; set; } = string.Empty;
    public string Status { get; set; } = "Created";
    public int Tick { get; set; }
    public int MaxTicks { get; set; }
    public int Residents { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}

/// <summary>One workspace, with its latest snapshot for listing and after-restart inspection.
/// The live state (conversation, triggers, ledger) is the workspace grain's durable state.</summary>
public sealed class WorkspaceRecord
{
    /// <summary>Owning organization (docs/platform.md); rows from before tenancy belong to "default".</summary>
    public string TenantId { get; set; } = "default";
    public required string WorkspaceId { get; set; }
    public required string Name { get; set; }
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int Agents { get; set; }
    public int Triggers { get; set; }
    public long TotalTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}

/// <summary>An encrypted connection secret (see Secrets/SecretProtector). Never plaintext.</summary>
public sealed class SecretRecord
{
    public required string Scope { get; set; }
    public required string Key { get; set; }
    public required byte[] Ciphertext { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One audit log record (see Safety/SafetyContracts). Append-only; hash-chained per scope.</summary>
public sealed class AuditRecord
{
    public long Id { get; set; }
    public required string Scope { get; set; }
    public long Seq { get; set; }
    public required string Key { get; set; }
    public DateTimeOffset At { get; set; }
    public required string ActorType { get; set; }
    public required string ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public required string Action { get; set; }
    public string Target { get; set; } = string.Empty;
    public string? SideEffects { get; set; }
    public string Outcome { get; set; } = "ok";
    public string Summary { get; set; } = string.Empty;
    public string DetailJson { get; set; } = "{}";
    public required string PreviousHash { get; set; }
    public required string Hash { get; set; }
}

/// <summary>The latest record of each audit chain, locked while appending so records are sequenced one at a time.</summary>
public sealed class AuditHeadRecord
{
    public required string Scope { get; set; }
    public long Seq { get; set; }
    public required string Hash { get; set; }
}

// ---- Platform: organizations, people, access, billing (docs/platform.md) ----

/// <summary>An organization: the unit of isolation, membership and billing.</summary>
public sealed class TenantRecord
{
    public required string TenantId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string SubscriptionStatus { get; set; } = "none";
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
}

public sealed class UserRecord
{
    public required string UserId { get; set; }
    /// <summary>Lower-cased and trimmed; unique.</summary>
    public required string Email { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>PBKDF2 (see Identity/PasswordHasher); never the password.</summary>
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class MembershipRecord
{
    public required string TenantId { get; set; }
    public required string UserId { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A signed-in browser. Only a hash of the cookie's token is stored.</summary>
public sealed class SessionRecord
{
    public required string TokenHash { get; set; }
    public required string UserId { get; set; }
    /// <summary>The organization the session is currently working in.</summary>
    public required string TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>A key for the API/SDK, bound to one organization and a role. Only a hash of the
/// secret is stored; the full key is shown once, when it's created.</summary>
public sealed class ApiKeyRecord
{
    public required string KeyId { get; set; }
    public required string TenantId { get; set; }
    public required string Name { get; set; }
    /// <summary>The first characters of the key, for recognising it in lists.</summary>
    public required string Prefix { get; set; }
    public required string SecretHash { get; set; }
    public required string Role { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class InvitationRecord
{
    public required string InvitationId { get; set; }
    public required string TenantId { get; set; }
    public required string Email { get; set; }
    public required string Role { get; set; }
    public required string TokenHash { get; set; }
    public string InvitedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Billing-provider webhook events already applied, so a redelivery is a no-op.</summary>
public sealed class BillingEventRecord
{
    public required string EventId { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
