using System.Collections.Concurrent;

namespace AgentRuntime.Studies;

/// <summary>Study state kept in memory: tests and the standalone host. The infrastructure layer
/// replaces it with PostgreSQL.</summary>
public sealed class InMemoryStudyStore : IStudyStore
{
    private readonly ConcurrentDictionary<string, StudyInfo> _studies = new();
    private readonly ConcurrentDictionary<string, StudyDataset> _datasets = new();
    private readonly ConcurrentDictionary<string, StudyEvidence> _evidence = new();
    private readonly ConcurrentDictionary<string, StudyHypothesis> _hypotheses = new();
    private readonly ConcurrentDictionary<string, StudyModel> _models = new();
    private readonly ConcurrentDictionary<(string, string), SourceRoleEntry> _roles = new();
    private readonly ConcurrentDictionary<string, StudyReport> _reports = new();
    private readonly ConcurrentDictionary<string, StudySimulation> _simulations = new();
    private readonly ConcurrentDictionary<string, StudyAssumption> _assumptions = new();
    private readonly ConcurrentDictionary<string, StudyProjection> _projections = new();

    public void AddStudy(StudyInfo study) => _studies[study.StudyId] = study;

    public Task<StudyInfo?> GetAsync(string studyId, CancellationToken ct = default) => Task.FromResult(_studies.GetValueOrDefault(studyId));

    public Task<StudyInfo?> GetByWorkspaceAsync(string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(_studies.Values.FirstOrDefault(s => s.WorkspaceId == workspaceId));

    public Task<IReadOnlyList<StudyDataset>> ListDatasetsAsync(string studyId, bool currentOnly = true, CancellationToken ct = default)
    {
        var all = _datasets.Values.Where(d => d.StudyId == studyId).ToList();
        IReadOnlyList<StudyDataset> result = currentOnly
            ? all.GroupBy(d => d.Name).Select(g => g.MaxBy(d => d.Version)!).OrderBy(d => d.Name).ToList()
            : all.OrderBy(d => d.Name).ThenBy(d => d.Version).ToList();
        return Task.FromResult(result);
    }

    public Task<StudyDataset?> GetDatasetAsync(string studyId, string name, CancellationToken ct = default) =>
        Task.FromResult(_datasets.Values.Where(d => d.StudyId == studyId && d.Name == name).MaxBy(d => d.Version));

    public Task AddDatasetAsync(StudyDataset dataset, CancellationToken ct = default)
    {
        _datasets[dataset.DatasetId] = dataset;
        return Task.CompletedTask;
    }

    public Task AddEvidenceAsync(StudyEvidence evidence, CancellationToken ct = default)
    {
        _evidence[evidence.EvidenceId] = evidence;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StudyEvidence>> GetEvidenceAsync(string studyId, IReadOnlyCollection<string> evidenceIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StudyEvidence>>(evidenceIds.Select(id => _evidence.GetValueOrDefault(id)).OfType<StudyEvidence>()
            .Where(e => e.StudyId == studyId).ToList());

    public Task<IReadOnlyDictionary<string, int>> CountEvidenceBySourceAsync(string studyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(_evidence.Values.Where(e => e.StudyId == studyId && e.SourceKey is not null)
            .SelectMany(e => e.SourceKey!.Split(',')).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count()));

    public Task AddHypothesisAsync(StudyHypothesis hypothesis, CancellationToken ct = default)
    {
        _hypotheses[hypothesis.HypothesisId] = hypothesis;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StudyHypothesis>> ListHypothesesAsync(string studyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StudyHypothesis>>(_hypotheses.Values.Where(h => h.StudyId == studyId).OrderBy(h => h.CreatedAt).ToList());

    public Task AddModelAsync(StudyModel model, CancellationToken ct = default)
    {
        _models[model.ModelId] = model;
        return Task.CompletedTask;
    }

    public Task<StudyModel?> GetModelAsync(string studyId, string modelId, CancellationToken ct = default) =>
        Task.FromResult(_models.TryGetValue(modelId, out var m) && m.StudyId == studyId ? m : null);

    public Task UpdateModelAsync(StudyModel model, CancellationToken ct = default) => AddModelAsync(model, ct);

    public Task<IReadOnlyList<StudyModel>> ListModelsAsync(string studyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StudyModel>>(_models.Values.Where(m => m.StudyId == studyId).OrderBy(m => m.CreatedAt).ToList());

    public Task<int> CountHoldoutEvaluationsAsync(string studyId, CancellationToken ct = default) =>
        Task.FromResult(_models.Values.Count(m => m.StudyId == studyId && m.HoldoutJson is not null));

    public Task SetSourceRoleAsync(SourceRoleEntry entry, CancellationToken ct = default)
    {
        _roles[(entry.StudyId, entry.SourceKey)] = entry;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SourceRoleEntry>> ListSourceRolesAsync(string studyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceRoleEntry>>(_roles.Values.Where(r => r.StudyId == studyId).ToList());

    public Task SaveReportAsync(StudyReport report, CancellationToken ct = default)
    {
        _reports[report.RunId] = report;
        return Task.CompletedTask;
    }

    public Task<StudyReport?> GetReportAsync(string runId, CancellationToken ct = default) => Task.FromResult(_reports.GetValueOrDefault(runId));

    public Task AddSimulationAsync(StudySimulation simulation, CancellationToken ct = default)
    {
        _simulations[simulation.SimulationId] = simulation;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StudySimulation>> ListSimulationsAsync(string studyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StudySimulation>>(_simulations.Values.Where(s => s.StudyId == studyId).OrderBy(s => s.CreatedAt).ToList());

    public Task AddAssumptionAsync(StudyAssumption assumption, CancellationToken ct = default)
    {
        foreach (var old in _assumptions.Values.Where(a => a.StudyId == assumption.StudyId && a.Name == assumption.Name && a.Current))
        {
            _assumptions[old.AssumptionId] = old with { Current = false };
        }

        _assumptions[assumption.AssumptionId] = assumption with { Current = true };
        return Task.CompletedTask;
    }

    public Task UpdateAssumptionAsync(StudyAssumption assumption, CancellationToken ct = default)
    {
        _assumptions[assumption.AssumptionId] = assumption;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StudyAssumption>> ListAssumptionsAsync(string studyId, bool currentOnly = true, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StudyAssumption>>(_assumptions.Values.Where(a => a.StudyId == studyId && (!currentOnly || a.Current))
            .OrderBy(a => a.CreatedAt).ToList());

    public Task AddProjectionAsync(StudyProjection projection, CancellationToken ct = default)
    {
        _projections[projection.ProjectionId] = projection;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StudyProjection>> ListProjectionsAsync(string studyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StudyProjection>>(_projections.Values.Where(p => p.StudyId == studyId).OrderBy(p => p.CreatedAt).ToList());
}

/// <summary>No sandbox configured (the standalone host): analyses fail with a clear reason.</summary>
public sealed class UnavailableAnalysisSandbox : IAnalysisSandbox
{
    public Task<AnalysisOutcome> RunAsync(AnalysisJob job, CancellationToken ct = default)
    {
        const string error = "No analysis sandbox is configured on this server.";
        return Task.FromResult(new AnalysisOutcome
        {
            Ok = false,
            Error = error,
            Result = new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["error"] = error }
        });
    }
}
