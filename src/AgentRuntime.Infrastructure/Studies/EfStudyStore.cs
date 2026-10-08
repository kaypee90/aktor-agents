using System.Text.Json;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Studies;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Studies;

/// <summary>Study state in PostgreSQL (docs/studies.md). Every query is scoped to one study.</summary>
public sealed class EfStudyStore(IDbContextFactory<AgentDbContext> dbFactory) : IStudyStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StudyInfo?> GetAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return Info(await db.Studies.AsNoTracking().FirstOrDefaultAsync(s => s.StudyId == studyId, ct));
    }

    public async Task<StudyInfo?> GetByWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return Info(await db.Studies.AsNoTracking().FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct));
    }

    private static StudyInfo? Info(StudyRecord? r) => r is null ? null : new StudyInfo
    {
        StudyId = r.StudyId, TenantId = r.TenantId, WorkspaceId = r.WorkspaceId, Name = r.Name, Question = r.Question
    };

    public async Task<IReadOnlyList<StudyDataset>> ListDatasetsAsync(string studyId, bool currentOnly = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.StudyDatasets.AsNoTracking()
            .Where(d => d.StudyId == studyId && (!currentOnly || d.Current))
            .OrderBy(d => d.Name).ThenBy(d => d.Version)
            .ToListAsync(ct);
        return rows.Select(ToDataset).ToList();
    }

    public async Task<StudyDataset?> GetDatasetAsync(string studyId, string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.StudyDatasets.AsNoTracking().Where(d => d.StudyId == studyId && d.Name == name && d.Current).FirstOrDefaultAsync(ct);
        return row is null ? null : ToDataset(row);
    }

    public async Task AddDatasetAsync(StudyDataset dataset, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.StudyDatasets.Where(d => d.StudyId == dataset.StudyId && d.Name == dataset.Name && d.Current)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Current, false), ct);
        db.StudyDatasets.Add(new StudyDatasetRecord
        {
            DatasetId = dataset.DatasetId,
            StudyId = dataset.StudyId,
            Name = dataset.Name,
            FileName = dataset.FileName,
            Version = dataset.Version,
            Kind = dataset.Kind,
            Rows = dataset.Rows,
            TrainRows = dataset.TrainRows,
            HoldoutRows = dataset.HoldoutRows,
            TimeColumn = dataset.TimeColumn,
            HoldoutFraction = dataset.HoldoutFraction,
            ProfileJson = dataset.ProfileJson,
            DictionaryJson = JsonSerializer.Serialize(dataset.Dictionary, Json),
            SizeBytes = dataset.SizeBytes,
            Current = true,
            CreatedAt = dataset.CreatedAt
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>People edit the data dictionary; it stays with the current version.</summary>
    public async Task<bool> SetDictionaryAsync(string studyId, string datasetId, Dictionary<string, string> dictionary, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(dictionary, Json);
        return await db.StudyDatasets.Where(d => d.StudyId == studyId && d.DatasetId == datasetId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.DictionaryJson, json), ct) > 0;
    }

    /// <summary>Removes a dataset (every version); returns their ids so the files can go too.</summary>
    public async Task<IReadOnlyList<string>> DeleteDatasetAsync(string studyId, string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.StudyDatasets.Where(d => d.StudyId == studyId && d.Name == name).Select(d => d.DatasetId).ToListAsync(ct);
        await db.StudyDatasets.Where(d => d.StudyId == studyId && d.Name == name).ExecuteDeleteAsync(ct);
        await db.StudySourceRoles.Where(r => r.StudyId == studyId && r.SourceKey == StudyRefs.DatasetKey(name)).ExecuteDeleteAsync(ct);
        return ids;
    }

    private static StudyDataset ToDataset(StudyDatasetRecord d) => new()
    {
        DatasetId = d.DatasetId,
        StudyId = d.StudyId,
        Name = d.Name,
        FileName = d.FileName,
        Version = d.Version,
        Kind = d.Kind,
        Rows = d.Rows,
        TrainRows = d.TrainRows,
        HoldoutRows = d.HoldoutRows,
        TimeColumn = d.TimeColumn,
        HoldoutFraction = d.HoldoutFraction,
        ProfileJson = d.ProfileJson,
        Dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(d.DictionaryJson, Json) ?? [],
        SizeBytes = d.SizeBytes,
        CreatedAt = d.CreatedAt
    };

    public async Task AddEvidenceAsync(StudyEvidence e, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.StudyEvidence.Add(new StudyEvidenceRecord
        {
            EvidenceId = e.EvidenceId, StudyId = e.StudyId, RunId = e.RunId, AgentId = e.AgentId, Kind = e.Kind,
            SourceKey = e.SourceKey, Summary = e.Summary, DetailJson = e.DetailJson, CreatedAt = e.CreatedAt
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StudyEvidence>> GetEvidenceAsync(string studyId, IReadOnlyCollection<string> evidenceIds, CancellationToken ct = default)
    {
        if (evidenceIds.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = evidenceIds.ToList();
        var rows = await db.StudyEvidence.AsNoTracking().Where(e => e.StudyId == studyId && ids.Contains(e.EvidenceId)).ToListAsync(ct);
        return rows.Select(ToEvidence).ToList();
    }

    /// <summary>A study's evidence, newest first.</summary>
    public async Task<IReadOnlyList<StudyEvidence>> ListEvidenceAsync(string studyId, int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.StudyEvidence.AsNoTracking().Where(e => e.StudyId == studyId)
            .OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync(ct);
        return rows.Select(ToEvidence).ToList();
    }

    private static StudyEvidence ToEvidence(StudyEvidenceRecord e) => new()
    {
        EvidenceId = e.EvidenceId, StudyId = e.StudyId, RunId = e.RunId, AgentId = e.AgentId, Kind = e.Kind,
        SourceKey = e.SourceKey, Summary = e.Summary, DetailJson = e.DetailJson, CreatedAt = e.CreatedAt
    };

    public async Task<IReadOnlyDictionary<string, int>> CountEvidenceBySourceAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = await db.StudyEvidence.AsNoTracking().Where(e => e.StudyId == studyId && e.SourceKey != null)
            .Select(e => e.SourceKey!).ToListAsync(ct);
        return keys.SelectMany(k => k.Split(',')).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task AddHypothesisAsync(StudyHypothesis h, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.StudyHypotheses.Add(new StudyHypothesisRecord
        {
            HypothesisId = h.HypothesisId, StudyId = h.StudyId, RunId = h.RunId, AgentId = h.AgentId,
            Statement = h.Statement, Rationale = h.Rationale, CreatedAt = h.CreatedAt
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StudyHypothesis>> ListHypothesesAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.StudyHypotheses.AsNoTracking().Where(h => h.StudyId == studyId).OrderBy(h => h.CreatedAt).ToListAsync(ct))
            .Select(h => new StudyHypothesis
            {
                HypothesisId = h.HypothesisId, StudyId = h.StudyId, RunId = h.RunId, AgentId = h.AgentId,
                Statement = h.Statement, Rationale = h.Rationale, CreatedAt = h.CreatedAt
            }).ToList();
    }

    public async Task AddModelAsync(StudyModel m, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.StudyModels.Add(ToRecord(m));
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateModelAsync(StudyModel m, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.StudyModels.Update(ToRecord(m));
        await db.SaveChangesAsync(ct);
    }

    public async Task<StudyModel?> GetModelAsync(string studyId, string modelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.StudyModels.AsNoTracking().FirstOrDefaultAsync(m => m.StudyId == studyId && m.ModelId == modelId, ct);
        return row is null ? null : ToModel(row);
    }

    public async Task<IReadOnlyList<StudyModel>> ListModelsAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.StudyModels.AsNoTracking().Where(m => m.StudyId == studyId).OrderBy(m => m.CreatedAt).ToListAsync(ct)).Select(ToModel).ToList();
    }

    public async Task<int> CountHoldoutEvaluationsAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.StudyModels.CountAsync(m => m.StudyId == studyId && m.HoldoutJson != null, ct);
    }

    private static StudyModelRecord ToRecord(StudyModel m) => new()
    {
        ModelId = m.ModelId, StudyId = m.StudyId, RunId = m.RunId, AgentId = m.AgentId, HypothesisId = m.HypothesisId,
        Method = m.Method, DatasetId = m.DatasetId, DatasetName = m.DatasetName, DatasetVersion = m.DatasetVersion, Target = m.Target,
        FeaturesJson = JsonSerializer.Serialize(m.Features, Json), OptionsJson = m.OptionsJson, ResultJson = m.ResultJson,
        EvidenceId = m.EvidenceId, Status = m.Status, ReviewerAgentId = m.ReviewerAgentId, ReviewNotes = m.ReviewNotes,
        ReviewedAt = m.ReviewedAt, HoldoutJson = m.HoldoutJson, HoldoutEvidenceId = m.HoldoutEvidenceId, CreatedAt = m.CreatedAt
    };

    private static StudyModel ToModel(StudyModelRecord m) => new()
    {
        ModelId = m.ModelId, StudyId = m.StudyId, RunId = m.RunId, AgentId = m.AgentId, HypothesisId = m.HypothesisId,
        Method = m.Method, DatasetId = m.DatasetId, DatasetName = m.DatasetName, DatasetVersion = m.DatasetVersion, Target = m.Target,
        Features = JsonSerializer.Deserialize<List<string>>(m.FeaturesJson, Json) ?? [], OptionsJson = m.OptionsJson, ResultJson = m.ResultJson,
        EvidenceId = m.EvidenceId, Status = m.Status, ReviewerAgentId = m.ReviewerAgentId, ReviewNotes = m.ReviewNotes,
        ReviewedAt = m.ReviewedAt, HoldoutJson = m.HoldoutJson, HoldoutEvidenceId = m.HoldoutEvidenceId, CreatedAt = m.CreatedAt
    };

    public async Task SetSourceRoleAsync(SourceRoleEntry entry, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.StudySourceRoles.FindAsync([entry.StudyId, entry.SourceKey], ct);
        if (row is null)
        {
            db.StudySourceRoles.Add(new StudySourceRoleRecord
            {
                StudyId = entry.StudyId, SourceKey = entry.SourceKey, Role = entry.Role.ToString(),
                Reason = entry.Reason, AgentId = entry.AgentId, UpdatedAt = entry.UpdatedAt
            });
        }
        else
        {
            row.Role = entry.Role.ToString();
            row.Reason = entry.Reason;
            row.AgentId = entry.AgentId;
            row.UpdatedAt = entry.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SourceRoleEntry>> ListSourceRolesAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.StudySourceRoles.AsNoTracking().Where(r => r.StudyId == studyId).ToListAsync(ct))
            .Select(r => new SourceRoleEntry
            {
                StudyId = r.StudyId, SourceKey = r.SourceKey,
                Role = Enum.TryParse<SourceRole>(r.Role, out var role) ? role : SourceRole.Unassigned,
                Reason = r.Reason, AgentId = r.AgentId, UpdatedAt = r.UpdatedAt
            }).ToList();
    }

    public async Task SaveReportAsync(StudyReport report, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.StudyReports.FindAsync([report.RunId], ct);
        if (row is null)
        {
            db.StudyReports.Add(new StudyReportRecord
            {
                RunId = report.RunId, StudyId = report.StudyId, AgentId = report.AgentId, Json = report.Json, CreatedAt = report.CreatedAt
            });
        }
        else
        {
            row.Json = report.Json;
            row.AgentId = report.AgentId;
            row.CreatedAt = report.CreatedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<StudyReport?> GetReportAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var r = await db.StudyReports.AsNoTracking().FirstOrDefaultAsync(x => x.RunId == runId, ct);
        return r is null ? null : new StudyReport { RunId = r.RunId, StudyId = r.StudyId, AgentId = r.AgentId, Json = r.Json, CreatedAt = r.CreatedAt };
    }

    /// <summary>The study's reports, newest first.</summary>
    public async Task<IReadOnlyList<StudyReport>> ListReportsAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.StudyReports.AsNoTracking().Where(r => r.StudyId == studyId).OrderByDescending(r => r.CreatedAt).ToListAsync(ct))
            .Select(r => new StudyReport { RunId = r.RunId, StudyId = r.StudyId, AgentId = r.AgentId, Json = r.Json, CreatedAt = r.CreatedAt }).ToList();
    }

    public async Task AddSimulationAsync(StudySimulation s, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.StudySimulations.Add(new StudySimulationRecord
        {
            SimulationId = s.SimulationId, StudyId = s.StudyId, RunId = s.RunId, AgentId = s.AgentId, Name = s.Name,
            SpecJson = s.SpecJson, SummaryJson = s.SummaryJson, DatasetName = s.DatasetName, EvidenceId = s.EvidenceId,
            Participants = s.Participants, Decisions = s.Decisions, Tokens = s.Tokens, CostUsd = s.CostUsd,
            DurationMs = s.DurationMs, CreatedAt = s.CreatedAt
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StudySimulation>> ListSimulationsAsync(string studyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.StudySimulations.AsNoTracking().Where(s => s.StudyId == studyId).OrderBy(s => s.CreatedAt).ToListAsync(ct))
            .Select(s => new StudySimulation
            {
                SimulationId = s.SimulationId, StudyId = s.StudyId, RunId = s.RunId, AgentId = s.AgentId, Name = s.Name,
                SpecJson = s.SpecJson, SummaryJson = s.SummaryJson, DatasetName = s.DatasetName, EvidenceId = s.EvidenceId,
                Participants = s.Participants, Decisions = s.Decisions, Tokens = s.Tokens, CostUsd = s.CostUsd,
                DurationMs = s.DurationMs, CreatedAt = s.CreatedAt
            }).ToList();
    }
}
