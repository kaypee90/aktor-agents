using System.Text.Json.Nodes;

namespace AgentRuntime.Studies;

/// <summary>Ids of studies and of their runs (docs/studies.md).</summary>
public static class StudyIds
{
    public const string Prefix = "study-";
    public const string RunPrefix = "srun-";

    public static string New() => Prefix + Guid.NewGuid().ToString("n")[..10];
    public static string NewRun() => RunPrefix + Guid.NewGuid().ToString("n");

    public static bool IsStudy(string? id) => id?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>A study run is a task whose id starts with "srun-": its agents work inside the
    /// study's workspace but organize themselves like any task's (no pipeline helper rules).</summary>
    public static bool IsRun(string? taskId) => taskId?.StartsWith(RunPrefix, StringComparison.Ordinal) == true;

    /// <summary>A short id for evidence, models, hypotheses and simulations: "ev-3f9a1c2b".</summary>
    public static string Short(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("n")[..8]}";
}

/// <summary>What a source is for, in the study's data-use plan.</summary>
public enum SourceRole
{
    Unassigned,
    /// <summary>Data models are fitted on.</summary>
    ModelInput,
    /// <summary>Real rates a simulation is checked against.</summary>
    Calibration,
    /// <summary>Who a simulated population consists of (segments and their attributes).</summary>
    Population,
    /// <summary>Facts the scenario is built from.</summary>
    Scenario,
    /// <summary>Data that checks a model's or a finding's predictions.</summary>
    Validation,
    /// <summary>Not used, with a reason.</summary>
    NotRelevant
}

/// <summary>Kinds of evidence: what produced it.</summary>
public static class EvidenceKinds
{
    public const string Query = "query";
    public const string Model = "model";
    public const string Analysis = "analysis";
    public const string Holdout = "holdout";
    public const string Passage = "passage";
    public const string Connection = "connection";
    public const string Simulation = "simulation";
}

/// <summary>Sources are named "dataset:name", "document:file.pdf" or "connection:name".</summary>
public static class SourceKeys
{
    public static string Dataset(string name) => $"dataset:{name}";
    public static string Document(string fileName) => $"document:{fileName}";
    public static string Connection(string name) => $"connection:{name}";
}

public sealed record StudyInfo
{
    public required string StudyId { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Name { get; init; }
    public string Question { get; init; } = string.Empty;
}

/// <summary>A dataset of a study: an uploaded file (cleaned, profiled and split into training data
/// and the sealed holdout) or the decisions of a simulation.</summary>
public sealed record StudyDataset
{
    public required string DatasetId { get; init; }
    public required string StudyId { get; init; }
    /// <summary>The table name agents use in SQL and tools (lowercase, underscores).</summary>
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public int Version { get; init; } = 1;
    /// <summary>"uploaded" or "simulated".</summary>
    public string Kind { get; init; } = "uploaded";
    public long Rows { get; init; }
    public long TrainRows { get; init; }
    public long HoldoutRows { get; init; }
    public string? TimeColumn { get; init; }
    public double HoldoutFraction { get; init; }
    /// <summary>The profile from ingestion: columns with kind, missing values, ranges, top values.</summary>
    public string ProfileJson { get; init; } = "{}";
    /// <summary>What each column means, written by people: column → description.</summary>
    public Dictionary<string, string> Dictionary { get; init; } = [];
    public long SizeBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One piece of evidence: something the runtime ran or fetched for a study, with what came
/// back. Findings cite evidence by id.</summary>
public sealed record StudyEvidence
{
    public string EvidenceId { get; init; } = StudyIds.Short("ev");
    public required string StudyId { get; init; }
    public string? RunId { get; init; }
    public required string AgentId { get; init; }
    public required string Kind { get; init; }
    /// <summary>The source it came from (see <see cref="SourceKeys"/>), for the data coverage table.</summary>
    public string? SourceKey { get; init; }
    public required string Summary { get; init; }
    /// <summary>What was run (query, code, spec) and what came back.</summary>
    public string DetailJson { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record StudyHypothesis
{
    public string HypothesisId { get; init; } = StudyIds.Short("hyp");
    public required string StudyId { get; init; }
    public string? RunId { get; init; }
    public required string AgentId { get; init; }
    public required string Statement { get; init; }
    public string? Rationale { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A candidate model in the study's registry.</summary>
public sealed record StudyModel
{
    public string ModelId { get; init; } = StudyIds.Short("mdl");
    public required string StudyId { get; init; }
    public string? RunId { get; init; }
    /// <summary>The agent that fitted it; it can't review its own model.</summary>
    public required string AgentId { get; init; }
    public string? HypothesisId { get; init; }
    public required string Method { get; init; }
    /// <summary>The exact dataset version it was fitted on (holdout scoring uses the same one).</summary>
    public required string DatasetId { get; init; }
    public required string DatasetName { get; init; }
    public int DatasetVersion { get; init; }
    public required string Target { get; init; }
    public List<string> Features { get; init; } = [];
    public string OptionsJson { get; init; } = "{}";
    /// <summary>The fit: coefficients, metrics, diagnostics, warnings.</summary>
    public string ResultJson { get; init; } = "{}";
    public required string EvidenceId { get; init; }
    /// <summary>"candidate", "accepted" or "rejected".</summary>
    public string Status { get; init; } = "candidate";
    public string? ReviewerAgentId { get; init; }
    public string? ReviewNotes { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? HoldoutJson { get; init; }
    public string? HoldoutEvidenceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SourceRoleEntry
{
    public required string StudyId { get; init; }
    public required string SourceKey { get; init; }
    public required SourceRole Role { get; init; }
    public string? Reason { get; init; }
    public required string AgentId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A run's report as submitted, after the runtime checked it (<see cref="StudyReportValidator"/>).</summary>
public sealed record StudyReport
{
    public required string RunId { get; init; }
    public required string StudyId { get; init; }
    public required string AgentId { get; init; }
    public required string Json { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record StudySimulation
{
    public string SimulationId { get; init; } = StudyIds.Short("sim");
    public required string StudyId { get; init; }
    public string? RunId { get; init; }
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public string SpecJson { get; init; } = "{}";
    public string SummaryJson { get; init; } = "{}";
    /// <summary>The simulated dataset holding every decision.</summary>
    public required string DatasetName { get; init; }
    public required string EvidenceId { get; init; }
    public int Participants { get; init; }
    public int Decisions { get; init; }
    public long Tokens { get; init; }
    public decimal CostUsd { get; init; }
    public long DurationMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One job for the analysis sandbox: the job document and the files it reads.</summary>
public sealed record AnalysisJob
{
    public required JsonObject Job { get; init; }
    /// <summary>Path inside the job (e.g. "data/sales.parquet") → file on this machine.</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
}

public sealed record AnalysisOutcome
{
    public required bool Ok { get; init; }
    public required JsonObject Result { get; init; }
    public string? Error { get; init; }
    /// <summary>Files the job wrote (charts, an ingested dataset's split), by name.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; init; } = new Dictionary<string, byte[]>();
    public long DurationMs { get; init; }
}
