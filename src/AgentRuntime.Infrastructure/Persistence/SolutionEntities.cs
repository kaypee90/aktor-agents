namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>A solution installed in an organization (docs/solutions.md): the package as uploaded,
/// the values it was installed with, and the workspaces it made.</summary>
public sealed class SolutionInstallRecord
{
    public required string InstallId { get; set; }
    public string TenantId { get; set; } = "default";
    /// <summary>The manifest's name; one active install per name in an organization.</summary>
    public required string Name { get; set; }
    public string Title { get; set; } = string.Empty;
    public required string Version { get; set; }
    /// <summary>"Active" or "Uninstalled".</summary>
    public string Status { get; set; } = "Active";
    /// <summary>The uploaded <c>SolutionBundle</c> as JSON (manifest text and files).</summary>
    public string BundleJson { get; set; } = "{}";
    /// <summary>The parsed manifest as JSON, what the runtime reads.</summary>
    public string ManifestJson { get; set; } = "{}";
    /// <summary>Input values (not secrets).</summary>
    public string InputsJson { get; set; } = "{}";
    /// <summary>Workspace key → workspace id.</summary>
    public string WorkspacesJson { get; set; } = "{}";
    /// <summary>Versions installed before, oldest first: [{version, at, by}].</summary>
    public string HistoryJson { get; set; } = "[]";
    public string? InstalledBy { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One record of a solution's data model. Data is JSONB, validated against the entity's
/// fields on every write.</summary>
public sealed class SolutionRecordEntity
{
    public required string RecordId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string InstallId { get; set; }
    public required string Entity { get; set; }
    public string DataJson { get; set; } = "{}";
    /// <summary>The end user it belongs to (its end_user field), for scoping.</summary>
    public string? EndUserId { get; set; }
    public int Version { get; set; } = 1;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The record-change outbox: written with the record, delivered to record triggers and
/// webhooks by the dispatcher, then marked delivered.</summary>
public sealed class SolutionRecordChangeEntity
{
    public long ChangeId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string InstallId { get; set; }
    public required string Entity { get; set; }
    public required string RecordId { get; set; }
    public required string Event { get; set; }
    public string DataJson { get; set; } = "{}";
    public string? PreviousJson { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    /// <summary>The task or run the change was made in, if an agent made it (for evals).</summary>
    public string? TaskId { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    /// <summary>When the solution's workspaces were given the change; webhook retries don't give it again.</summary>
    public DateTimeOffset? WorkspacesNotifiedAt { get; set; }
    /// <summary>When a failed webhook delivery is tried again.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>An end user's conversation with a solution's assistant (a task they own).</summary>
public sealed class SolutionConversationRecord
{
    public required string TaskId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string InstallId { get; set; }
    public required string EndUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A published version of a solution package in the registry (docs/solutions.md#the-registry).</summary>
public sealed class SolutionPackageRecord
{
    /// <summary>"name@version".</summary>
    public required string PackageId { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public required string PublisherTenantId { get; set; }
    public string PublisherName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>"public" (every organization on this server) or "organization" (the publisher's only).</summary>
    public string Visibility { get; set; } = "organization";
    public string BundleJson { get; set; } = "{}";
    /// <summary>SHA-256 of the bundle as stored, hex.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>The registry's signature over name, version and digest, hex: what installs verify.</summary>
    public string Signature { get; set; } = string.Empty;
    public bool Yanked { get; set; }
    public string? YankReason { get; set; }
    public int Installs { get; set; }
    public string? PublishedBy { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}

/// <summary>Who owns a package name: the first organization to publish it. Only it publishes new versions.</summary>
public sealed class SolutionPackageNameRecord
{
    public required string Name { get; set; }
    public required string TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
