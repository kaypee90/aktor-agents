namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>A study (docs/studies.md): its question, and the workspace that holds its connections,
/// knowledge, safety policy and daily budget.</summary>
public sealed class StudyRecord
{
    public required string StudyId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string WorkspaceId { get; set; }
    public required string Name { get; set; }
    public string Question { get; set; } = string.Empty;
    /// <summary>"Draft" until the first run, then the last run's: "Running", "Completed", "Failed".</summary>
    public string Status { get; set; } = "Draft";
    public string? LastRunId { get; set; }
    /// <summary>The organization model profile runs use unless one is picked for a run; null for the default.</summary>
    public string? ModelProfileId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StudyDatasetRecord
{
    public required string DatasetId { get; set; }
    public required string StudyId { get; set; }
    public required string Name { get; set; }
    public required string FileName { get; set; }
    public int Version { get; set; }
    public string Kind { get; set; } = "uploaded";
    public long Rows { get; set; }
    public long TrainRows { get; set; }
    public long HoldoutRows { get; set; }
    public string? TimeColumn { get; set; }
    public double HoldoutFraction { get; set; }
    public string ProfileJson { get; set; } = "{}";
    public string DictionaryJson { get; set; } = "{}";
    public long SizeBytes { get; set; }
    /// <summary>False once a newer version of the same dataset exists.</summary>
    public bool Current { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StudyEvidenceRecord
{
    public required string EvidenceId { get; set; }
    public required string StudyId { get; set; }
    public string? RunId { get; set; }
    public required string AgentId { get; set; }
    public required string Kind { get; set; }
    public string? SourceKey { get; set; }
    public required string Summary { get; set; }
    public string DetailJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StudyHypothesisRecord
{
    public required string HypothesisId { get; set; }
    public required string StudyId { get; set; }
    public string? RunId { get; set; }
    public required string AgentId { get; set; }
    public required string Statement { get; set; }
    public string? Rationale { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StudyModelRecord
{
    public required string ModelId { get; set; }
    public required string StudyId { get; set; }
    public string? RunId { get; set; }
    public required string AgentId { get; set; }
    public string? HypothesisId { get; set; }
    public required string Method { get; set; }
    public required string DatasetId { get; set; }
    public required string DatasetName { get; set; }
    public int DatasetVersion { get; set; }
    public required string Target { get; set; }
    public string FeaturesJson { get; set; } = "[]";
    public string OptionsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public required string EvidenceId { get; set; }
    public string Status { get; set; } = "candidate";
    public string? ReviewerAgentId { get; set; }
    public string? ReviewNotes { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? HoldoutJson { get; set; }
    public string? HoldoutEvidenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StudySourceRoleRecord
{
    public required string StudyId { get; set; }
    public required string SourceKey { get; set; }
    public required string Role { get; set; }
    public string? Reason { get; set; }
    public required string AgentId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StudyReportRecord
{
    public required string RunId { get; set; }
    public required string StudyId { get; set; }
    public required string AgentId { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StudySimulationRecord
{
    public required string SimulationId { get; set; }
    public required string StudyId { get; set; }
    public string? RunId { get; set; }
    public required string AgentId { get; set; }
    public required string Name { get; set; }
    public string SpecJson { get; set; } = "{}";
    public string SummaryJson { get; set; } = "{}";
    public required string DatasetName { get; set; }
    public required string EvidenceId { get; set; }
    public int Participants { get; set; }
    public int Decisions { get; set; }
    public long Tokens { get; set; }
    public decimal CostUsd { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A workspace template an organization made from one of its workspaces (docs/templates.md):
/// the workspace's definition without secrets, chat, runs or files.</summary>
public sealed class OrganizationTemplateRecord
{
    public required string TemplateId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string Name { get; set; }
    public string Category { get; set; } = "Custom";
    public string Description { get; set; } = string.Empty;
    /// <summary>The <c>WorkspaceDefinition</c> as JSON.</summary>
    public string DefinitionJson { get; set; } = "{}";
    public string? SampleInput { get; set; }
    public string? SourceWorkspaceId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
