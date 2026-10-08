namespace AgentRuntime.Studies;

/// <summary>
/// Durable study state (docs/studies.md): datasets, evidence, hypotheses, the model registry, the
/// data-use plan, reports and simulations. PostgreSQL is the source of truth; every read is scoped
/// to one study, and a study belongs to one organization.
/// </summary>
public interface IStudyStore
{
    Task<StudyInfo?> GetAsync(string studyId, CancellationToken ct = default);

    /// <summary>The study a workspace belongs to, or null for an ordinary workspace.</summary>
    Task<StudyInfo?> GetByWorkspaceAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>The current version of each dataset (or every version).</summary>
    Task<IReadOnlyList<StudyDataset>> ListDatasetsAsync(string studyId, bool currentOnly = true, CancellationToken ct = default);
    Task<StudyDataset?> GetDatasetAsync(string studyId, string name, CancellationToken ct = default);
    /// <summary>Adds a dataset, or a new version of one with the same name (the old ones stay, no
    /// longer current, so earlier evidence still names what it used).</summary>
    Task AddDatasetAsync(StudyDataset dataset, CancellationToken ct = default);

    Task AddEvidenceAsync(StudyEvidence evidence, CancellationToken ct = default);
    Task<IReadOnlyList<StudyEvidence>> GetEvidenceAsync(string studyId, IReadOnlyCollection<string> evidenceIds, CancellationToken ct = default);
    /// <summary>How many pieces of evidence came from each source, for the data coverage table.</summary>
    Task<IReadOnlyDictionary<string, int>> CountEvidenceBySourceAsync(string studyId, CancellationToken ct = default);

    Task AddHypothesisAsync(StudyHypothesis hypothesis, CancellationToken ct = default);
    Task<IReadOnlyList<StudyHypothesis>> ListHypothesesAsync(string studyId, CancellationToken ct = default);

    Task AddModelAsync(StudyModel model, CancellationToken ct = default);
    Task<StudyModel?> GetModelAsync(string studyId, string modelId, CancellationToken ct = default);
    Task UpdateModelAsync(StudyModel model, CancellationToken ct = default);
    Task<IReadOnlyList<StudyModel>> ListModelsAsync(string studyId, CancellationToken ct = default);
    /// <summary>Holdout evaluations so far, against <see cref="StudyOptions.MaxHoldoutEvaluations"/>.</summary>
    Task<int> CountHoldoutEvaluationsAsync(string studyId, CancellationToken ct = default);

    Task SetSourceRoleAsync(SourceRoleEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<SourceRoleEntry>> ListSourceRolesAsync(string studyId, CancellationToken ct = default);

    Task SaveReportAsync(StudyReport report, CancellationToken ct = default);
    Task<StudyReport?> GetReportAsync(string runId, CancellationToken ct = default);

    Task AddSimulationAsync(StudySimulation simulation, CancellationToken ct = default);
    Task<IReadOnlyList<StudySimulation>> ListSimulationsAsync(string studyId, CancellationToken ct = default);
}

/// <summary>Runs one analysis job in an isolated container: no network, nothing from the host
/// but the job's own files, memory, CPU and time limits (CLAUDE.md section 19).</summary>
public interface IAnalysisSandbox
{
    Task<AnalysisOutcome> RunAsync(AnalysisJob job, CancellationToken ct = default);
}

/// <summary>Where a study's files live on this machine.</summary>
public static class StudyPaths
{
    public static string StudyRoot(StudyOptions options, string studyId)
    {
        if (!StudyIds.IsStudy(studyId) || studyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new ArgumentException("Not a study id.", nameof(studyId));
        }

        return Path.GetFullPath(Path.Combine(options.DataRoot, studyId));
    }

    public static string DatasetDir(StudyOptions options, string studyId, string datasetId) =>
        Path.Combine(StudyRoot(options, studyId), "datasets", Safe(datasetId));

    public static string TrainFile(StudyOptions options, StudyDataset d) => Path.Combine(DatasetDir(options, d.StudyId, d.DatasetId), "train.parquet");
    public static string HoldoutFile(StudyOptions options, StudyDataset d) => Path.Combine(DatasetDir(options, d.StudyId, d.DatasetId), "holdout.parquet");

    /// <summary>Charts and other files analyses produced.</summary>
    public static string FilesDir(StudyOptions options, string studyId) => Path.Combine(StudyRoot(options, studyId), "files");

    private static string Safe(string segment) =>
        segment.Length > 0 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? segment
            : throw new ArgumentException("Unsafe path segment.", nameof(segment));
}
