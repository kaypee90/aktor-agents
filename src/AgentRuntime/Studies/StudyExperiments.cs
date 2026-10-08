using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Logging;

namespace AgentRuntime.Studies;

/// <summary>What an experiment left behind, or why it didn't.</summary>
public sealed record ExperimentRecord(StudySimulation? Simulation, JsonObject? Summary, string? Error)
{
    public static ExperimentRecord Fail(string error) => new(null, null, error);
}

/// <summary>An experiment a person started that hasn't been saved yet: in progress, or failed.</summary>
public sealed record PendingExperiment
{
    public required string ExperimentId { get; init; }
    public required string StudyId { get; init; }
    public required string Name { get; init; }
    public required string StartedBy { get; init; }
    public int Planned { get; init; }
    public int Done { get; set; }
    /// <summary>"running" or "failed".</summary>
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Runs an experiment and records it in its study (docs/studies.md, "Experiments"): the decisions
/// become a simulated dataset, the outcome evidence, and the experiment a row of the study. Agents
/// get here through run_simulation; people through the Experiments tab, where the experiment runs
/// in the background and its progress is kept here until it's saved.
/// </summary>
public sealed class StudyExperiments(StudyToolSupport support, ExperimentRunner runner, IGrainFactory grains, ILogger<StudyExperiments> logger)
{
    /// <summary>The agent id manual experiments are recorded under.</summary>
    public const string ManualAgentId = "person";

    private readonly ConcurrentDictionary<string, PendingExperiment> _pending = new();

    /// <summary>Runs <paramref name="spec"/> (parsed from <paramref name="args"/>) and records it.
    /// The cited evidence must belong to the study, and the study's daily budget must allow it.</summary>
    public async Task<ExperimentRecord> RunAsync(StudyInfo study, JsonObject args, ExperimentSpec spec, IReadOnlyList<string> notes,
        string? runId, string agentId, CancellationToken ct, string? profileId = null, IProgress<int>? progress = null)
    {
        if (await CheckAsync(study, spec, ct) is { } refused) return ExperimentRecord.Fail(refused);
        var cited = spec.CitedEvidence.ToList();
        var known = (await support.Store.GetEvidenceAsync(study.StudyId, cited, ct)).ToDictionary(e => e.EvidenceId);

        var outcome = await runner.RunAsync(spec, study.TenantId, runId, study.WorkspaceId, agentId, ct, profileId, progress);
        // Charged like any model call: to the study's daily budget and the organization's plan.
        await grains.GetGrain<IWorkspaceGrain>(study.WorkspaceId).RecordUsage(agentId, outcome.Tokens, outcome.CostUsd);
        await grains.GetGrain<Tenancy.ITenantGrain>(Tenancy.TenantIds.Normalize(study.TenantId)).RecordUsage(new Tenancy.UsageDelta
        {
            Tokens = outcome.Tokens, CostUsd = outcome.CostUsd, LlmCalls = outcome.Calls
        });
        if (outcome.Rows.Count == 0) return ExperimentRecord.Fail("No participant made a decision; the simulation produced no data.");

        // The decisions become a dataset like any other (no holdout: it's the experiment's output).
        var existing = await support.Store.ListDatasetsAsync(study.StudyId, currentOnly: false, ct);
        var tableName = UniqueTable(existing, "sim_" + ExperimentRunner.ColumnName(spec.Name));
        var dataset = await StudyIngest.IngestRowsAsync(support, study, tableName, $"{spec.Name} (simulated)", outcome.Rows, ct);
        if (dataset is null) return ExperimentRecord.Fail("The simulation ran, but its decisions couldn't be saved as a dataset.");

        var sources = known.Values.Select(e => e.SourceKey).OfType<string>().SelectMany(k => k.Split(',')).Append(StudyRefs.DatasetKey(dataset.Name)).Distinct();
        var evidence = await support.RecordAsync(study, runId, agentId, EvidenceKinds.Simulation,
            $"Simulation '{spec.Name}': {outcome.Participants} participants, {outcome.Rows.Count} decisions",
            new { spec = args, summary = outcome.Summary, dataset = dataset.Name, notes }, string.Join(",", sources), ct);
        var simulation = new StudySimulation
        {
            SimulationId = StudyIds.Short("sim"),
            StudyId = study.StudyId,
            RunId = runId,
            AgentId = agentId,
            Name = spec.Name,
            SpecJson = args.ToJsonString(),
            SummaryJson = outcome.Summary.ToJsonString(),
            DatasetName = dataset.Name,
            EvidenceId = evidence.EvidenceId,
            Participants = outcome.Participants,
            Decisions = outcome.Rows.Count,
            Tokens = outcome.Tokens,
            CostUsd = outcome.CostUsd,
            DurationMs = outcome.DurationMs
        };
        await support.Store.AddSimulationAsync(simulation, ct);
        return new ExperimentRecord(simulation, outcome.Summary, null);
    }

    /// <summary>Why the experiment can't run now (unknown evidence, no budget left); null when it can.</summary>
    public async Task<string?> CheckAsync(StudyInfo study, ExperimentSpec spec, CancellationToken ct)
    {
        // The population and facts must come from this study's evidence.
        var cited = spec.CitedEvidence.ToList();
        var known = (await support.Store.GetEvidenceAsync(study.StudyId, cited, ct)).Select(e => e.EvidenceId).ToHashSet();
        var missing = cited.Where(id => !known.Contains(id)).ToList();
        if (missing.Count > 0) return $"These evidence ids don't exist in this study: {string.Join(", ", missing)}.";

        var budget = await grains.GetGrain<IWorkspaceGrain>(study.WorkspaceId).CheckBudget();
        return budget.Allowed ? null : $"The study's daily budget doesn't allow a simulation now: {budget.Reason}";
    }

    /// <summary>Starts an experiment a person set up, in the background (one at a time per study).
    /// Returns its pending entry; once saved, it's an experiment of the study like any other.</summary>
    public async Task<(PendingExperiment? Pending, string? Error)> StartManualAsync(StudyInfo study, JsonObject args, ExperimentSpec spec,
        IReadOnlyList<string> notes, string? profileId, string startedBy, CancellationToken stopping)
    {
        if (_pending.Values.Any(p => p.StudyId == study.StudyId && p.Status == "running"))
        {
            return (null, "An experiment of this study is still running.");
        }

        if (await CheckAsync(study, spec, stopping) is { } refused) return (null, refused);

        var pending = new PendingExperiment
        {
            ExperimentId = StudyIds.Short("exp"),
            StudyId = study.StudyId,
            Name = spec.Name,
            StartedBy = startedBy,
            Planned = ExperimentRunner.PlannedDecisions(spec)
        };
        _pending[pending.ExperimentId] = pending;
        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<int>(done => pending.Done = Math.Max(pending.Done, done));
                var record = await RunAsync(study, args, spec, notes, runId: null, ManualAgentId, stopping, profileId, progress);
                if (record.Error is null)
                {
                    _pending.TryRemove(pending.ExperimentId, out _);
                    return;
                }

                pending.Error = record.Error;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Manual experiment {ExperimentId} of study {StudyId} failed", pending.ExperimentId, study.StudyId);
                pending.Error = stopping.IsCancellationRequested ? "The server stopped before the experiment finished." : ex.Message;
            }

            pending.Status = "failed";
        }, CancellationToken.None);
        return (pending, null);
    }

    /// <summary>The study's experiments started by people that aren't saved yet.</summary>
    public IReadOnlyList<PendingExperiment> Pending(string studyId) =>
        _pending.Values.Where(p => p.StudyId == studyId).OrderBy(p => p.StartedAt).ToList();

    /// <summary>Dismisses a failed experiment; one still running stays.</summary>
    public bool Dismiss(string studyId, string experimentId) =>
        _pending.TryGetValue(experimentId, out var p) && p.StudyId == studyId && p.Status == "failed" && _pending.TryRemove(experimentId, out _);

    /// <summary>Forgets a deleted study's pending experiments.</summary>
    public void Forget(string studyId)
    {
        foreach (var p in _pending.Values.Where(p => p.StudyId == studyId && p.Status != "running")) _pending.TryRemove(p.ExperimentId, out _);
    }

    private static string UniqueTable(IReadOnlyList<StudyDataset> existing, string name)
    {
        name = name.Length > 40 ? name[..40].TrimEnd('_') : name;
        var candidate = name;
        for (var i = 2; existing.Any(d => d.Name == candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }
}
