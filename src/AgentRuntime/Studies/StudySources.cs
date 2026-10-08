using AgentRuntime.Memory;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Studies;

/// <summary>Everything a study can draw on, and the data-use plan for it.</summary>
public sealed record StudySourcesView
{
    public required IReadOnlyList<StudyDataset> Datasets { get; init; }
    public required IReadOnlyList<StudyDataset> SimulatedDatasets { get; init; }
    public required KnowledgeSummary Documents { get; init; }
    public required IReadOnlyList<(string Name, IReadOnlyList<string> Tools)> Connections { get; init; }
    public required IReadOnlyDictionary<string, SourceRoleEntry> Roles { get; init; }

    /// <summary>Every source that needs a role: uploaded datasets, documents (by file; typed facts
    /// together as "document:facts") and connections. Simulated datasets are results, not sources.</summary>
    public IReadOnlyList<string> SourceKeys =>
    [
        .. Datasets.Select(d => StudyRefs.DatasetKey(d.Name)),
        .. Documents.FileNames.Select(StudyRefs.DocumentKey),
        .. Documents.Facts > 0 ? [StudyRefs.FactsKey] : Array.Empty<string>(),
        .. Connections.Select(c => StudyRefs.ConnectionKey(c.Name)),
    ];

    public IReadOnlyList<string> Unassigned =>
        SourceKeys.Where(k => !Roles.TryGetValue(k, out var r) || r.Role == SourceRole.Unassigned).ToList();
}

public static class StudyRefs
{
    public const string FactsKey = "document:facts";
    public static string DatasetKey(string name) => SourceKeys.Dataset(name);
    public static string DocumentKey(string file) => SourceKeys.Document(file);
    public static string ConnectionKey(string name) => SourceKeys.Connection(name);

    /// <summary>"model_input" → <see cref="SourceRole.ModelInput"/>.</summary>
    public static SourceRole? ParseRole(string? role) => role?.Trim().ToLowerInvariant().Replace("-", "_") switch
    {
        "model_input" or "modelinput" => SourceRole.ModelInput,
        "calibration" => SourceRole.Calibration,
        "population" => SourceRole.Population,
        "scenario" or "scenario_facts" => SourceRole.Scenario,
        "validation" => SourceRole.Validation,
        "not_relevant" or "notrelevant" => SourceRole.NotRelevant,
        _ => null
    };

    public static string RoleName(SourceRole role) => role switch
    {
        SourceRole.ModelInput => "model_input",
        SourceRole.NotRelevant => "not_relevant",
        _ => role.ToString().ToLowerInvariant()
    };
}

/// <summary>Reads a study's sources from where they live: datasets from the study store,
/// documents from the study workspace's knowledge, connections from the workspace.</summary>
public sealed class StudySources(IStudyStore store, IMemoryStore memory, IGrainFactory grains)
{
    public async Task<StudySourcesView> GetAsync(StudyInfo study, CancellationToken ct = default)
    {
        var datasets = await store.ListDatasetsAsync(study.StudyId, currentOnly: true, ct);
        var keys = await memory.ListSharedKeysAsync(study.TenantId, MemoryScope.OnlyWorkspace(study.WorkspaceId), ct);
        // Knowledge agents saved during runs is the study's own output, not a source.
        var documents = KnowledgeSummary.Of(keys.Where(k => k.AgentId == "user").ToList());
        var connections = await grains.GetGrain<IWorkspaceGrain>(study.WorkspaceId).ListConnections();
        var roles = await store.ListSourceRolesAsync(study.StudyId, ct);
        return new StudySourcesView
        {
            Datasets = datasets.Where(d => d.Kind != "simulated").ToList(),
            SimulatedDatasets = datasets.Where(d => d.Kind == "simulated").ToList(),
            Documents = documents,
            Connections = connections.Select(c => (c.Name, (IReadOnlyList<string>)c.Tools.Where(t => t.Enabled).Select(t => t.Name).ToList())).ToList(),
            Roles = roles.GroupBy(r => r.SourceKey).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt).First())
        };
    }
}
