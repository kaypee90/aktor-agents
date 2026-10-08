using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Studies;
using AgentRuntime.Memory;
using AgentRuntime.Studies;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

public sealed class StudyServiceException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Studies (docs/studies.md): creating one (with its own workspace for connections, knowledge,
/// safety and budget), its datasets, runs, evidence and the notebook export. Everything is scoped
/// to the caller's organization: another organization's study answers as missing.
/// </summary>
public sealed class StudyService(
    IDbContextFactory<AgentDbContext> dbFactory,
    EfStudyStore store,
    IAnalysisSandbox sandbox,
    StudySources sources,
    IMemoryStore memory,
    IGrainFactory grains,
    TaskService tasks,
    LLM.LlmSettingsService modelSettings,
    IOptions<StudyOptions> options,
    ILogger<StudyService> logger)
{
    private StudyOptions O => options.Value;

    /// <summary>A model profile id to keep on a study: null (or empty) for the organization's default,
    /// else a profile of the organization or "server". Unknown ones are refused.</summary>
    private async Task<string?> CheckModelAsync(string tenantId, string? model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var (found, profile) = (await modelSettings.GetAsync(tenantId, ct)).Find(model);
        if (!found) throw new StudyServiceException($"No model '{model}'. Pick one under Settings → AI model.");
        return profile?.Id ?? LLM.ModelProfiles.ServerId;
    }

    public static readonly string[] DatasetExtensions = [".csv", ".tsv", ".txt", ".xlsx", ".xlsm", ".parquet", ".json", ".jsonl", ".ndjson"];

    public async Task<StudyRecord> RequireAsync(string tenantId, string studyId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Studies.AsNoTracking().FirstOrDefaultAsync(s => s.StudyId == studyId && s.TenantId == tenantId, ct)
               ?? throw new StudyServiceException($"No study '{studyId}'.", StatusCodes.Status404NotFound);
    }

    private static StudyInfo Info(StudyRecord s) => new() { StudyId = s.StudyId, TenantId = s.TenantId, WorkspaceId = s.WorkspaceId, Name = s.Name, Question = s.Question };

    public async Task<IReadOnlyList<object>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var studies = await db.Studies.AsNoTracking().Where(s => s.TenantId == tenantId).OrderByDescending(s => s.UpdatedAt).Take(200).ToListAsync(ct);
        var ids = studies.Select(s => s.StudyId).ToList();
        var datasets = await db.StudyDatasets.AsNoTracking().Where(d => ids.Contains(d.StudyId) && d.Current && d.Kind != "simulated")
            .GroupBy(d => d.StudyId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var runIds = studies.Select(s => s.LastRunId).OfType<string>().ToList();
        var runs = await db.Tasks.AsNoTracking().Where(t => runIds.Contains(t.TaskId)).ToDictionaryAsync(t => t.TaskId, ct);
        return studies.Select(s => (object)new
        {
            study_id = s.StudyId,
            name = s.Name,
            question = s.Question,
            status = StatusOf(s, s.LastRunId is { } r ? runs.GetValueOrDefault(r) : null),
            datasets = datasets.GetValueOrDefault(s.StudyId),
            created_at = s.CreatedAt,
            updated_at = s.UpdatedAt
        }).ToList();
    }

    private static string StatusOf(StudyRecord s, TaskRecord? lastRun) => lastRun is null ? "Draft"
        : lastRun.CompletedAt is null ? "Running"
        : lastRun.Status is "Failed" or "TimedOut" or "Terminated" or "Canceled" ? "Failed"
        : "Completed";

    public async Task<StudyRecord> CreateAsync(string tenantId, string name, string? question, string createdBy, CancellationToken ct, string? model = null)
    {
        var modelProfileId = await CheckModelAsync(tenantId, model, ct);
        name = name.Trim();
        if (name.Length is 0 or > 120) throw new StudyServiceException("A study needs a name of up to 120 characters.");
        if (question?.Length > 4000) throw new StudyServiceException("Keep the question under 4,000 characters.");

        // The study's workspace holds its connections, knowledge, safety policy and daily budget.
        var workspaceId = WorkspaceIds.New();
        await grains.GetGrain<IWorkspaceGrain>(workspaceId).Create(new WorkspaceCreationRequest
        {
            Name = $"Study: {name}",
            Goal = question?.Trim() is { Length: > 0 } q ? q : name,
            TenantId = tenantId,
            OwnerId = createdBy,
            CreatedBy = createdBy,
            DailyTokenLimit = O.DailyTokenLimit,
            DailyCostLimitUsd = O.DailyCostLimitUsd,
            // Research reads; anything a connection would change needs a person's approval.
            SafetyPolicy = new Safety.WorkspaceSafetyPolicy
            {
                Rules = [new Safety.ApprovalRule { Name = "Connected tools that change things", ToolPattern = "*__*", Applies = Safety.SideEffectScope.Writes }]
            }
        });

        var now = DateTimeOffset.UtcNow;
        var study = new StudyRecord
        {
            StudyId = StudyIds.New(),
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            Name = name,
            Question = question?.Trim() ?? string.Empty,
            ModelProfileId = modelProfileId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Studies.Add(study);
        await db.SaveChangesAsync(ct);
        await db.Workspaces.Where(w => w.WorkspaceId == workspaceId).ExecuteUpdateAsync(u => u.SetProperty(w => w.Kind, "study"), ct);
        return study;
    }

    /// <summary>Renames, rewords the question, or picks the study's model (<paramref name="model"/>
    /// "" goes back to the organization's default; null leaves it).</summary>
    public async Task UpdateAsync(string tenantId, string studyId, string? name, string? question, CancellationToken ct, string? model = null)
    {
        await RequireAsync(tenantId, studyId, ct);
        if (name is not null && name.Trim().Length is 0 or > 120) throw new StudyServiceException("A study needs a name of up to 120 characters.");
        var modelProfileId = model is null ? null : await CheckModelAsync(tenantId, model, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Studies.FirstAsync(s => s.StudyId == studyId, ct);
        if (name is not null) row.Name = name.Trim();
        if (question is not null) row.Question = question.Trim();
        if (model is not null) row.ModelProfileId = modelProfileId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the study and everything in it: datasets and charts, evidence, models,
    /// reports, its documents and its workspace (connections and their secrets).</summary>
    public async Task DeleteAsync(string tenantId, string studyId, CancellationToken ct)
    {
        var study = await RequireAsync(tenantId, studyId, ct);
        var workspace = grains.GetGrain<IWorkspaceGrain>(study.WorkspaceId);
        foreach (var connection in await workspace.ListConnections()) await workspace.RemoveConnection(connection.ConnectionId);
        await workspace.Archive();
        foreach (var file in KnowledgeSummary.Of(await memory.ListSharedKeysAsync(tenantId, MemoryScope.OnlyWorkspace(study.WorkspaceId), ct)).FileNames)
        {
            await memory.DeleteSharedAsync(tenantId, MemoryScope.OnlyWorkspace(study.WorkspaceId), fileName: file, cancellationToken: ct);
        }

        var rest = await memory.ListSharedKeysAsync(tenantId, MemoryScope.OnlyWorkspace(study.WorkspaceId), ct);
        if (rest.Count > 0)
        {
            var all = await memory.SearchAsync(tenantId, string.Empty, Contracts.MemoryKind.Shared, scope: MemoryScope.OnlyWorkspace(study.WorkspaceId), cancellationToken: ct);
            await memory.DeleteSharedAsync(tenantId, MemoryScope.OnlyWorkspace(study.WorkspaceId), memoryIds: all.Select(m => m.MemoryId).ToList(), cancellationToken: ct);
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.StudyDatasets.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
            await db.StudyEvidence.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
            await db.StudyHypotheses.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
            await db.StudyModels.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
            await db.StudySourceRoles.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
            await db.StudyReports.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
            await db.StudySimulations.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
            await db.Studies.Where(x => x.StudyId == studyId).ExecuteDeleteAsync(ct);
        }

        try
        {
            var root = StudyPaths.StudyRoot(O, studyId);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't remove the files of study {StudyId}", studyId);
        }
    }

    // ---------------------------------------------------------------- datasets

    public sealed record DatasetUpload(string FileName, long SizeBytes, Func<Stream> Open);

    public async Task<IReadOnlyList<object>> AddDatasetsAsync(string tenantId, string studyId, IReadOnlyList<DatasetUpload> files, CancellationToken ct)
    {
        var study = await RequireAsync(tenantId, studyId, ct);
        if (files.Count == 0) throw new StudyServiceException("Choose at least one file.");
        var current = await store.ListDatasetsAsync(studyId, currentOnly: true, ct);
        var results = new List<object>();
        foreach (var file in files)
        {
            var name = TaskService.SafeFileName(file.FileName);
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (!DatasetExtensions.Contains(extension))
            {
                results.Add(new { file_name = name, error = $"Use CSV, TSV, Excel (.xlsx), Parquet or JSON; '{extension}' isn't a dataset format. Documents go under Documents." });
                continue;
            }

            if (file.SizeBytes > O.MaxDatasetBytes)
            {
                results.Add(new { file_name = name, error = $"Larger than {O.MaxDatasetBytes / (1024 * 1024)} MB." });
                continue;
            }

            var table = StudyIngest.TableNameFor(name);
            if (current.All(d => d.Name != table) && current.Count(d => d.Kind != "simulated") >= O.MaxDatasetsPerStudy)
            {
                results.Add(new { file_name = name, error = $"A study holds at most {O.MaxDatasetsPerStudy} datasets." });
                continue;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"aktor-dataset-{Guid.NewGuid():n}{extension}");
            try
            {
                await using (var target = File.Create(temp))
                await using (var source = file.Open())
                {
                    await source.CopyToAsync(target, ct);
                }

                var previous = current.FirstOrDefault(d => d.Name == table);
                var result = await StudyIngest.IngestAsync(store, sandbox, O, Info(study),
                    new StudyIngest.Request(name, temp, previous?.HoldoutFraction ?? O.DefaultHoldoutFraction, previous?.TimeColumn), ct);
                if (result.Dataset is null)
                {
                    results.Add(new { file_name = name, error = result.Error });
                    continue;
                }

                // The upload itself is kept, so the holdout can be re-split later.
                File.Copy(temp, Path.Combine(StudyPaths.DatasetDir(O, studyId, result.Dataset.DatasetId), "raw" + extension), overwrite: true);
                results.Add(new { file_name = name, error = (string?)null, dataset = DatasetView(result.Dataset) });
            }
            finally
            {
                File.Delete(temp);
            }
        }

        await TouchAsync(studyId, ct);
        return results;
    }

    /// <summary>Edits a dataset's dictionary; a new time column or holdout share re-splits it as a new version.</summary>
    public async Task<object> UpdateDatasetAsync(string tenantId, string studyId, string datasetId, Dictionary<string, string>? dictionary,
        string? timeColumn, double? holdoutFraction, CancellationToken ct)
    {
        var study = await RequireAsync(tenantId, studyId, ct);
        var dataset = (await store.ListDatasetsAsync(studyId, currentOnly: true, ct)).FirstOrDefault(d => d.DatasetId == datasetId)
                      ?? throw new StudyServiceException("No such dataset (or it isn't the current version).", StatusCodes.Status404NotFound);
        var cleaned = dictionary?.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim().Length > 500 ? kv.Value.Trim()[..500] : kv.Value.Trim());

        var resplit = (timeColumn is not null && timeColumn != (dataset.TimeColumn ?? string.Empty))
                      || (holdoutFraction is { } f && Math.Abs(f - dataset.HoldoutFraction) > 1e-9);
        if (!resplit)
        {
            if (cleaned is not null) await store.SetDictionaryAsync(studyId, datasetId, cleaned, ct);
            return DatasetView(dataset with { Dictionary = cleaned ?? dataset.Dictionary });
        }

        if (dataset.Kind == "simulated") throw new StudyServiceException("A simulated dataset has no holdout to re-split.");
        var dir = StudyPaths.DatasetDir(O, studyId, dataset.DatasetId);
        var raw = Directory.Exists(dir) ? Directory.GetFiles(dir, "raw.*").FirstOrDefault() : null;
        if (raw is null) throw new StudyServiceException("The original upload isn't kept for this dataset; upload the file again.");
        var result = await StudyIngest.IngestAsync(store, sandbox, O, Info(study), new StudyIngest.Request(dataset.FileName, raw,
            Math.Clamp(holdoutFraction ?? dataset.HoldoutFraction, 0, 0.5), string.IsNullOrWhiteSpace(timeColumn) ? null : timeColumn.Trim(),
            cleaned ?? dataset.Dictionary, TableName: dataset.Name), ct);
        if (result.Dataset is null) throw new StudyServiceException(result.Error ?? "Couldn't re-split the dataset.");
        File.Copy(raw, Path.Combine(StudyPaths.DatasetDir(O, studyId, result.Dataset.DatasetId), Path.GetFileName(raw)), overwrite: true);
        await TouchAsync(studyId, ct);
        return DatasetView(result.Dataset);
    }

    public async Task DeleteDatasetAsync(string tenantId, string studyId, string datasetId, CancellationToken ct)
    {
        await RequireAsync(tenantId, studyId, ct);
        var dataset = (await store.ListDatasetsAsync(studyId, currentOnly: false, ct)).FirstOrDefault(d => d.DatasetId == datasetId)
                      ?? throw new StudyServiceException("No such dataset.", StatusCodes.Status404NotFound);
        foreach (var id in await store.DeleteDatasetAsync(studyId, dataset.Name, ct))
        {
            var dir = StudyPaths.DatasetDir(O, studyId, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        await TouchAsync(studyId, ct);
    }

    /// <summary>The training rows of a dataset (the holdout stays sealed), as Parquet.</summary>
    public async Task<(string Path, string FileName)> DatasetFileAsync(string tenantId, string studyId, string datasetId, CancellationToken ct)
    {
        await RequireAsync(tenantId, studyId, ct);
        var dataset = (await store.ListDatasetsAsync(studyId, currentOnly: false, ct)).FirstOrDefault(d => d.DatasetId == datasetId)
                      ?? throw new StudyServiceException("No such dataset.", StatusCodes.Status404NotFound);
        return (StudyPaths.TrainFile(O, dataset), $"{dataset.Name}.parquet");
    }

    public static object DatasetView(StudyDataset d) => new
    {
        dataset_id = d.DatasetId,
        name = d.Name,
        file_name = d.FileName,
        version = d.Version,
        kind = d.Kind,
        rows = d.Rows,
        train_rows = d.TrainRows,
        holdout_rows = d.HoldoutRows,
        holdout_fraction = d.HoldoutFraction,
        time_column = d.TimeColumn,
        size_bytes = d.SizeBytes,
        profile = JsonNode.Parse(d.ProfileJson),
        dictionary = d.Dictionary,
        created_at = d.CreatedAt
    };

    // ---------------------------------------------------------------- detail

    public async Task<object> GetDetailAsync(string tenantId, string studyId, CancellationToken ct)
    {
        var study = await RequireAsync(tenantId, studyId, ct);
        var info = Info(study);
        var view = await sources.GetAsync(info, ct);
        var models = await store.ListModelsAsync(studyId, ct);
        var hypotheses = await store.ListHypothesesAsync(studyId, ct);
        var simulations = await store.ListSimulationsAsync(studyId, ct);
        var reports = await store.ListReportsAsync(studyId, ct);
        var counts = await store.CountEvidenceBySourceAsync(studyId, ct);
        var connections = await grains.GetGrain<IWorkspaceGrain>(study.WorkspaceId).ListConnections();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var runs = await db.Tasks.AsNoTracking().Where(t => t.TenantId == tenantId && t.Source == "study" && t.WorkspaceId == study.WorkspaceId)
            .OrderByDescending(t => t.CreatedAt).Take(20).ToListAsync(ct);
        var evidenceCount = await db.StudyEvidence.CountAsync(e => e.StudyId == studyId, ct);
        var latest = reports.FirstOrDefault();

        return new
        {
            study_id = study.StudyId,
            name = study.Name,
            question = study.Question,
            // The model runs use unless one is picked for a run; null: the organization's default.
            model_profile_id = study.ModelProfileId,
            workspace_id = study.WorkspaceId,
            status = StatusOf(study, runs.FirstOrDefault()),
            created_at = study.CreatedAt,
            updated_at = study.UpdatedAt,
            datasets = view.Datasets.Select(DatasetView),
            simulated_datasets = view.SimulatedDatasets.Select(DatasetView),
            documents = new { files = view.Documents.FileNames, facts = view.Documents.Facts },
            connections = connections.Select(c => new { connection_id = c.ConnectionId, name = c.Name, tools = c.Tools.Count(t => t.Enabled), error = c.LastError }),
            data_use_plan = view.SourceKeys.Select(k => new
            {
                source = k,
                role = view.Roles.TryGetValue(k, out var r) ? StudyRefs.RoleName(r.Role) : "unassigned",
                reason = view.Roles.TryGetValue(k, out var rr) ? rr.Reason : null,
                evidence_count = counts.GetValueOrDefault(k)
            }),
            runs = runs.Select(t => new
            {
                task_id = t.TaskId,
                status = t.CompletedAt is null ? "Running" : t.Status,
                instructions = t.Goal,
                created_at = t.CreatedAt,
                completed_at = t.CompletedAt,
                summary = t.ResultSummary,
                has_report = reports.Any(r => r.RunId == t.TaskId)
            }),
            models = models.Select(m => new
            {
                model_id = m.ModelId,
                method = m.Method,
                dataset = m.DatasetName,
                dataset_version = m.DatasetVersion,
                target = m.Target,
                features = m.Features,
                hypothesis_id = m.HypothesisId,
                author = m.AgentId,
                status = m.Status,
                reviewer = m.ReviewerAgentId,
                review_notes = m.ReviewNotes,
                result = JsonNode.Parse(m.ResultJson),
                holdout = m.HoldoutJson is null ? null : JsonNode.Parse(m.HoldoutJson),
                evidence_id = m.EvidenceId,
                holdout_evidence_id = m.HoldoutEvidenceId,
                created_at = m.CreatedAt
            }),
            hypotheses = hypotheses.Select(h => new { hypothesis_id = h.HypothesisId, statement = h.Statement, rationale = h.Rationale, agent_id = h.AgentId, created_at = h.CreatedAt }),
            simulations = simulations.Select(s => new
            {
                simulation_id = s.SimulationId,
                name = s.Name,
                dataset = s.DatasetName,
                participants = s.Participants,
                decisions = s.Decisions,
                cost_usd = s.CostUsd,
                summary = JsonNode.Parse(s.SummaryJson),
                evidence_id = s.EvidenceId,
                created_at = s.CreatedAt
            }),
            report = latest is null ? null : new { run_id = latest.RunId, created_at = latest.CreatedAt, content = JsonNode.Parse(latest.Json) },
            evidence_count = evidenceCount
        };
    }

    public async Task<object> GetEvidenceAsync(string tenantId, string studyId, string evidenceId, CancellationToken ct)
    {
        await RequireAsync(tenantId, studyId, ct);
        var e = (await store.GetEvidenceAsync(studyId, [evidenceId], ct)).FirstOrDefault()
                ?? throw new StudyServiceException("No such evidence in this study.", StatusCodes.Status404NotFound);
        return EvidenceView(e);
    }

    public async Task<IReadOnlyList<object>> ListEvidenceAsync(string tenantId, string studyId, CancellationToken ct)
    {
        await RequireAsync(tenantId, studyId, ct);
        return (await store.ListEvidenceAsync(studyId, 300, ct)).Select(EvidenceView).ToList();
    }

    private static object EvidenceView(StudyEvidence e) => new
    {
        evidence_id = e.EvidenceId,
        kind = e.Kind,
        run_id = e.RunId,
        agent_id = e.AgentId,
        sources = e.SourceKey?.Split(',') ?? [],
        summary = e.Summary,
        detail = JsonNode.Parse(e.DetailJson),
        created_at = e.CreatedAt
    };

    /// <summary>A chart an analysis saved (only names this study's own analyses wrote).</summary>
    public async Task<string> ChartPathAsync(string tenantId, string studyId, string name, CancellationToken ct)
    {
        await RequireAsync(tenantId, studyId, ct);
        if (name.Length > 100 || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new StudyServiceException("No such file.", StatusCodes.Status404NotFound);
        }

        var path = Path.Combine(StudyPaths.FilesDir(O, studyId), name);
        return File.Exists(path) ? path : throw new StudyServiceException("No such file.", StatusCodes.Status404NotFound);
    }

    // ---------------------------------------------------------------- runs

    /// <summary>Starts an agent team on the study's question. One run at a time.</summary>
    public async Task<TaskView> StartRunAsync(string tenantId, string studyId, string? instructions, string? startedBy, string? modelProfileId, CancellationToken ct)
    {
        var study = await RequireAsync(tenantId, studyId, ct);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (study.LastRunId is { } last && await db.Tasks.AnyAsync(t => t.TaskId == last && t.CompletedAt == null, ct))
            {
                throw new StudyServiceException("A run of this study is still in progress.", StatusCodes.Status409Conflict);
            }
        }

        var info = Info(study);
        var view = await sources.GetAsync(info, ct);
        if (view.Datasets.Count == 0 && view.Documents.FileNames.Count == 0 && view.Documents.Facts == 0 && view.Connections.Count == 0)
        {
            throw new StudyServiceException("Add a dataset, a document or a connection before running the study.");
        }

        var question = string.IsNullOrWhiteSpace(study.Question) ? study.Name : study.Question;
        var goal = $"Study \"{study.Name}\": {question}" + (string.IsNullOrWhiteSpace(instructions) ? string.Empty : $"\n\nFor this run: {instructions.Trim()}");
        var task = await tasks.StartAsync(tenantId, new StartTaskRequest
        {
            Goal = goal.Length > 8000 ? goal[..8000] : goal,
            Source = "study",
            StartedBy = startedBy,
            By = startedBy,
            ModelProfileId = string.IsNullOrWhiteSpace(modelProfileId) ? study.ModelProfileId : modelProfileId,
            Budget = new Contracts.ResourceBudget
            {
                MaxTokens = O.RunMaxTokens,
                MaxToolCalls = O.RunMaxToolCalls,
                MaxDurationSeconds = O.RunMaxDurationSeconds,
                MaxCostUsd = O.RunMaxCostUsd,
                MaxChildren = O.RunMaxChildren
            },
            MaxChildrenRequested = true,
            Study = new StudyRunLaunch(study.StudyId, study.WorkspaceId)
        }, ct);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.Studies.Where(s => s.StudyId == studyId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastRunId, task.TaskId)
                .SetProperty(s => s.Status, "Running")
                .SetProperty(s => s.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }

        return task;
    }

    private async Task TouchAsync(string studyId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Studies.Where(s => s.StudyId == studyId).ExecuteUpdateAsync(u => u.SetProperty(s => s.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    // ---------------------------------------------------------------- notebook

    /// <summary>
    /// A Jupyter notebook that reruns the study's analyses in order: every query, model fit,
    /// analysis and holdout score, with the code that produced it. It reads the datasets' training
    /// files (downloadable from the study) from a data/ folder next to it.
    /// </summary>
    public async Task<string> NotebookAsync(string tenantId, string studyId, CancellationToken ct)
    {
        var study = await RequireAsync(tenantId, studyId, ct);
        var datasets = await store.ListDatasetsAsync(studyId, currentOnly: true, ct);
        var evidence = (await store.ListEvidenceAsync(studyId, 1000, ct)).OrderBy(e => e.CreatedAt).ToList();
        var cells = new JsonArray
        {
            Markdown($"# {study.Name}\n\n{study.Question}\n\nExported from Aktor Studies on {DateTimeOffset.UtcNow:yyyy-MM-dd}. " +
                     "Download each dataset (Studies → Sources → Download) into a `data/` folder next to this notebook. " +
                     "They hold the training rows only; the sealed holdout isn't exported."),
            Code("""
                 import duckdb, numpy as np, pandas as pd, statsmodels.api as sm, statsmodels.formula.api as smf
                 import scipy, scipy.stats as stats, sympy, matplotlib.pyplot as plt
                 tables = {
                 """ + string.Join("\n", datasets.Select(d => $"    \"{d.Name}\": pd.read_parquet(\"data/{d.Name}.parquet\"),")) + """

                 }
                 con = duckdb.connect()
                 for name, frame in tables.items():
                     con.register(name, frame)
                 load = lambda name: tables[name].copy()
                 sql = lambda q: con.execute(q).df()
                 """)
        };

        foreach (var e in evidence)
        {
            JsonNode? detail;
            try { detail = JsonNode.Parse(e.DetailJson); } catch (JsonException) { continue; }
            switch (e.Kind)
            {
                case EvidenceKinds.Query when (detail?["sql"])?.ToString() is { } sql:
                    cells.Add(Markdown($"**{e.EvidenceId}** · query · {e.Summary}"));
                    cells.Add(Code($"sql(\"\"\"\n{sql}\n\"\"\")"));
                    break;
                case EvidenceKinds.Analysis when (detail?["code"])?.ToString() is { } code:
                    cells.Add(Markdown($"**{e.EvidenceId}** · analysis · {detail["purpose"]}"));
                    cells.Add(Code(code));
                    break;
                case EvidenceKinds.Model when detail is not null:
                    cells.Add(Markdown($"**{e.EvidenceId}** · model · {e.Summary}"));
                    cells.Add(Code(ModelCode(detail)));
                    break;
                case EvidenceKinds.Holdout:
                case EvidenceKinds.Simulation:
                case EvidenceKinds.Passage:
                case EvidenceKinds.Connection:
                    cells.Add(Markdown($"**{e.EvidenceId}** · {e.Kind} · {e.Summary}\n\n" +
                                       (e.Kind == EvidenceKinds.Holdout ? "Scored by the runtime on the sealed holdout, which isn't exported." : "Recorded by the runtime; see the study's evidence for the full result.")));
                    break;
            }
        }

        var notebook = new JsonObject
        {
            ["nbformat"] = 4,
            ["nbformat_minor"] = 5,
            ["metadata"] = new JsonObject { ["kernelspec"] = new JsonObject { ["name"] = "python3", ["display_name"] = "Python 3", ["language"] = "python" } },
            ["cells"] = cells
        };
        return notebook.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ModelCode(JsonNode detail)
    {
        var method = detail["method"]?.ToString();
        var dataset = detail["dataset"]?.ToString();
        var target = detail["target"]?.ToString();
        var features = detail["features"] is JsonArray f ? f.Select(x => x!.ToString()).ToList() : [];
        var options = detail["options"] as JsonObject ?? [];
        var sb = new StringBuilder($"df = load(\"{dataset}\")\n");
        switch (method)
        {
            case "arima":
                var order = options["order"]?.ToJsonString() ?? "[1, 1, 1]";
                var time = options["time_column"]?.ToString();
                if (time is not null) sb.AppendLine($"df = df.sort_values(\"{time}\")");
                sb.AppendLine("from statsmodels.tsa.arima.model import ARIMA");
                sb.AppendLine($"y = pd.to_numeric(df[\"{target}\"], errors=\"coerce\").dropna().reset_index(drop=True)");
                sb.AppendLine($"res = ARIMA(y.values, order=tuple({order})).fit()");
                sb.AppendLine("print(res.summary())");
                break;
            case "logistic_regression":
            case "linear_regression":
                var cols = string.Join(", ", features.Prepend(target!).Select(c => $"\"{c}\""));
                sb.AppendLine($"d = df[[{cols}]].dropna()");
                sb.AppendLine($"X = pd.get_dummies(d[[{string.Join(", ", features.Select(c => $"\"{c}\""))}]], drop_first=True, dtype=float)");
                sb.AppendLine("X = sm.add_constant(X.astype(float), has_constant=\"add\")");
                sb.AppendLine(method == "linear_regression"
                    ? $"res = sm.OLS(pd.to_numeric(d[\"{target}\"]), X).fit()"
                    : $"res = sm.Logit(d[\"{target}\"].astype(int), X).fit(disp=0)  # map a two-valued target to 0/1 first if needed");
                sb.AppendLine("print(res.summary())");
                break;
        }

        return sb.ToString();
    }

    private static JsonObject Markdown(string text) => new() { ["cell_type"] = "markdown", ["metadata"] = new JsonObject(), ["source"] = text };

    private static JsonObject Code(string code) => new()
    {
        ["cell_type"] = "code", ["metadata"] = new JsonObject(), ["execution_count"] = null, ["outputs"] = new JsonArray(), ["source"] = code
    };
}
