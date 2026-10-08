using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Studies;

/// <summary>What every study tool needs: the study the calling agent works in, its datasets as
/// sandbox tables, and a way to record evidence.</summary>
public sealed class StudyToolSupport(IStudyStore store, IAnalysisSandbox sandbox, StudySources sources, IOptions<StudyOptions> options)
{
    public IStudyStore Store => store;
    public IAnalysisSandbox Sandbox => sandbox;
    public StudySources Sources => sources;
    public StudyOptions Options => options.Value;

    /// <summary>The study of the calling agent's workspace; null outside a study (or another
    /// organization's, which can't happen through the runtime but is checked anyway).</summary>
    public async Task<StudyInfo?> StudyOfAsync(ToolExecutionRequest request)
    {
        if (request.WorkspaceId is not { } workspaceId) return null;
        var study = await store.GetByWorkspaceAsync(workspaceId, request.CancellationToken);
        return study is not null && Tenancy.TenantIds.Same(study.TenantId, request.TenantId) ? study : null;
    }

    public static ToolExecutionResult NotInStudy() => ToolExecutionResult.Fail("This tool works only inside a study run.");

    /// <summary>Every current dataset as a sandbox table (training rows only), by its name.</summary>
    public async Task<(JsonObject Tables, Dictionary<string, string> Files, IReadOnlyList<StudyDataset> Datasets)> TablesAsync(
        StudyInfo study, IEnumerable<string>? only, CancellationToken ct)
    {
        var all = await store.ListDatasetsAsync(study.StudyId, currentOnly: true, ct);
        var wanted = only?.Select(n => n.Trim().ToLowerInvariant()).ToHashSet();
        var chosen = wanted is { Count: > 0 } ? all.Where(d => wanted.Contains(d.Name)).ToList() : all.ToList();
        var tables = new JsonObject();
        var files = new Dictionary<string, string>();
        foreach (var d in chosen)
        {
            tables[d.Name] = $"data/{d.Name}.parquet";
            files[$"data/{d.Name}.parquet"] = StudyPaths.TrainFile(Options, d);
        }

        return (tables, files, chosen);
    }

    /// <summary>The datasets a SQL query or code mentions by name, for the coverage table.</summary>
    public static IReadOnlyList<StudyDataset> Mentioned(IReadOnlyList<StudyDataset> datasets, string text) =>
        datasets.Where(d => Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(d.Name)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)).ToList();

    public static string? SourceKeysOf(IEnumerable<StudyDataset> datasets)
    {
        var keys = datasets.Select(d => StudyRefs.DatasetKey(d.Name)).Distinct().ToList();
        return keys.Count == 0 ? null : string.Join(",", keys);
    }

    public async Task<StudyEvidence> RecordAsync(StudyInfo study, ToolExecutionRequest request, string kind, string summary, object detail, string? sourceKey)
    {
        var evidence = new StudyEvidence
        {
            StudyId = study.StudyId,
            RunId = request.TaskId,
            AgentId = request.AgentId,
            Kind = kind,
            SourceKey = sourceKey,
            Summary = summary.Length > 300 ? summary[..300] : summary,
            DetailJson = detail as string ?? JsonSerializer.Serialize(detail, ToolJson.Options)
        };
        await store.AddEvidenceAsync(evidence, request.CancellationToken);
        return evidence;
    }

    public static ToolExecutionResult Ok(object value) => ToolExecutionResult.Ok(JsonSerializer.Serialize(value, ToolJson.Options));

    public static JsonObject? Args(ToolExecutionRequest request) => StudyReportValidator.Parse(request.ArgumentsJson);

    public static string Str(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;
}

/// <summary>Lists what the study can draw on, the data-use plan, models and hypotheses.</summary>
public sealed class StudySourcesTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "study_sources",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "The study's question and sources: datasets (columns, types, ranges, data dictionary), documents, connections, " +
                      "the data-use plan (which sources still need a role), the models fitted so far and the hypotheses. Call it first.",
        JsonSchema = """{ "type": "object", "properties": {} }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var ct = request.CancellationToken;
        var view = await support.Sources.GetAsync(study, ct);
        var models = await support.Store.ListModelsAsync(study.StudyId, ct);
        var hypotheses = await support.Store.ListHypothesesAsync(study.StudyId, ct);
        var holdoutsUsed = await support.Store.CountHoldoutEvaluationsAsync(study.StudyId, ct);
        object Dataset(StudyDataset d) => new
        {
            table = d.Name,
            file = d.FileName,
            version = d.Version,
            rows_you_can_use = d.TrainRows,
            sealed_holdout_rows = d.HoldoutRows,
            time_column = d.TimeColumn,
            columns = JsonNode.Parse(d.ProfileJson)?["columns"],
            dictionary = d.Dictionary
        };
        return StudyToolSupport.Ok(new
        {
            study = new { name = study.Name, question = study.Question },
            datasets = view.Datasets.Select(Dataset),
            simulated_datasets = view.SimulatedDatasets.Select(Dataset),
            documents = new
            {
                files = view.Documents.FileNames,
                facts = view.Documents.Facts,
                how = "Search them with search_knowledge; each result comes with an evidence_id to cite."
            },
            connections = view.Connections.Select(c => new { name = c.Name, tools = c.Tools }),
            data_use_plan = view.SourceKeys.Select(k => new
            {
                source = k,
                role = view.Roles.TryGetValue(k, out var r) ? StudyRefs.RoleName(r.Role) : "unassigned",
                reason = view.Roles.TryGetValue(k, out var rr) ? rr.Reason : null
            }),
            sources_without_a_role = view.Unassigned,
            models = models.Select(m => new
            {
                model_id = m.ModelId, m.Method, dataset = m.DatasetName, m.Target, m.Features, author = m.AgentId, m.Status,
                review = m.ReviewNotes, holdout = m.HoldoutJson is null ? null : JsonNode.Parse(m.HoldoutJson)
            }),
            hypotheses = hypotheses.Select(h => new { hypothesis_id = h.HypothesisId, h.Statement, by = h.AgentId }),
            holdout_evaluations_left = Math.Max(0, support.Options.MaxHoldoutEvaluations - holdoutsUsed)
        });
    }
}

/// <summary>Read-only SQL over the study's training data.</summary>
public sealed class QueryDatasetTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "query_dataset",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Run one read-only SQL query (DuckDB dialect: SELECT or WITH ... SELECT) over the study's datasets; each dataset is a " +
                      "table named as in study_sources. Only training rows are visible; the holdout stays sealed. Returns up to 200 rows " +
                      "and an evidence_id to cite. Aggregate in SQL rather than reading raw rows.",
        JsonSchema = """{ "type": "object", "properties": { "sql": { "type": "string" } }, "required": ["sql"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        var sql = args is null ? string.Empty : StudyToolSupport.Str(args, "sql");
        if (sql.Length == 0) return ToolExecutionResult.Fail("sql is required.");
        var (tables, files, datasets) = await support.TablesAsync(study, null, request.CancellationToken);
        if (datasets.Count == 0) return ToolExecutionResult.Fail("This study has no datasets yet.");

        var outcome = await support.Sandbox.RunAsync(new AnalysisJob
        {
            Job = new JsonObject { ["op"] = "query", ["sql"] = sql, ["tables"] = tables, ["max_rows"] = support.Options.MaxQueryRows },
            Files = files
        }, request.CancellationToken);
        if (!outcome.Ok) return ToolExecutionResult.Fail($"Query failed: {outcome.Error}");

        var used = StudyToolSupport.Mentioned(datasets, sql);
        var r = outcome.Result;
        var evidence = await support.RecordAsync(study, request, EvidenceKinds.Query,
            $"Query over {string.Join(", ", used.Select(d => d.Name).DefaultIfEmpty("the datasets"))}: {r["row_count"]} row(s)",
            new { sql, datasets = used.Select(d => new { d.Name, d.Version }), result = r }, StudyToolSupport.SourceKeysOf(used));
        return StudyToolSupport.Ok(new
        {
            evidence_id = evidence.EvidenceId,
            columns = r["columns"],
            rows = r["rows"],
            row_count = r["row_count"],
            truncated = r["truncated"]
        });
    }
}

/// <summary>Pre-registers a hypothesis before it's tested.</summary>
public sealed class RecordHypothesisTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "record_hypothesis",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Record a hypothesis BEFORE fitting the model that tests it, so it's clear what was predicted rather than found " +
                      "after the fact. Pass the returned hypothesis_id to fit_model.",
        JsonSchema = """
        { "type": "object", "properties": {
            "statement": { "type": "string", "description": "e.g. 'Higher rent increases lower the chance of renewal.'" },
            "rationale": { "type": "string" } },
          "required": ["statement"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        var statement = args is null ? string.Empty : StudyToolSupport.Str(args, "statement");
        if (statement.Length == 0) return ToolExecutionResult.Fail("statement is required.");
        var hypothesis = new StudyHypothesis
        {
            StudyId = study.StudyId,
            RunId = request.TaskId,
            AgentId = request.AgentId,
            Statement = statement.Length > 1000 ? statement[..1000] : statement,
            Rationale = args is null ? null : StudyToolSupport.Str(args, "rationale") is { Length: > 0 } r ? r : null
        };
        await support.Store.AddHypothesisAsync(hypothesis, request.CancellationToken);
        return StudyToolSupport.Ok(new { hypothesis_id = hypothesis.HypothesisId });
    }
}

/// <summary>Fits a statistical model on training data and registers it as a candidate.</summary>
public sealed class FitModelTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "fit_model",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Fit a model on a dataset's training rows and register it as a candidate: linear_regression (numeric target), " +
                      "logistic_regression (two-valued target) or arima (a numeric series over time). Categorical features are " +
                      "dummy-coded. Returns coefficients with confidence intervals, fit statistics, cross-validation and diagnostics " +
                      "with warnings, plus a model_id and evidence_id. Another agent must review_model before findings rely on it.",
        JsonSchema = """
        { "type": "object", "properties": {
            "method": { "type": "string", "enum": ["linear_regression", "logistic_regression", "arima"] },
            "dataset": { "type": "string", "description": "Table name from study_sources" },
            "target": { "type": "string" },
            "features": { "type": "array", "items": { "type": "string" }, "description": "Columns to explain the target with (not for arima)" },
            "hypothesis_id": { "type": "string", "description": "The hypothesis this model tests (record_hypothesis)" },
            "options": { "type": "object", "properties": {
                "order": { "type": "array", "items": { "type": "integer" }, "description": "ARIMA [p, d, q]; default [1, 1, 1]" },
                "seasonal_order": { "type": "array", "items": { "type": "integer" }, "description": "ARIMA [P, D, Q, s]" },
                "horizon": { "type": "integer", "description": "ARIMA forecast steps; default 6" },
                "time_column": { "type": "string", "description": "Column that orders the series; defaults to the dataset's" },
                "positive_class": { "type": "string", "description": "Logistic: which target value counts as 1" } } } },
          "required": ["method", "dataset", "target"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        if (args is null) return ToolExecutionResult.Fail("Arguments must be a JSON object.");
        var method = StudyToolSupport.Str(args, "method");
        var datasetName = StudyToolSupport.Str(args, "dataset").ToLowerInvariant();
        var target = StudyToolSupport.Str(args, "target");
        var features = StudyReportValidator.Strings(args["features"]);
        if (method is not ("linear_regression" or "logistic_regression" or "arima")) return ToolExecutionResult.Fail("method is linear_regression, logistic_regression or arima.");
        if (method != "arima" && features.Count == 0) return ToolExecutionResult.Fail("features is required for regression.");
        var dataset = await support.Store.GetDatasetAsync(study.StudyId, datasetName, request.CancellationToken);
        if (dataset is null) return ToolExecutionResult.Fail($"No dataset '{datasetName}'. See study_sources.");

        var hypothesisId = StudyToolSupport.Str(args, "hypothesis_id");
        if (hypothesisId.Length > 0 && (await support.Store.ListHypothesesAsync(study.StudyId, request.CancellationToken)).All(h => h.HypothesisId != hypothesisId))
        {
            return ToolExecutionResult.Fail($"No hypothesis '{hypothesisId}' in this study.");
        }

        var modelOptions = args["options"] as JsonObject ?? [];
        if (method == "arima" && modelOptions["time_column"] is null && dataset.TimeColumn is { } tc) modelOptions["time_column"] = tc;
        var outcome = await support.Sandbox.RunAsync(new AnalysisJob
        {
            Job = new JsonObject
            {
                ["op"] = "fit", ["method"] = method, ["table"] = dataset.Name, ["target"] = target,
                ["features"] = new JsonArray(features.Select(f => (JsonNode)f!).ToArray()),
                ["options"] = modelOptions.DeepClone(),
                ["tables"] = new JsonObject { [dataset.Name] = $"data/{dataset.Name}.parquet" }
            },
            Files = new Dictionary<string, string> { [$"data/{dataset.Name}.parquet"] = StudyPaths.TrainFile(support.Options, dataset) }
        }, request.CancellationToken);
        if (!outcome.Ok) return ToolExecutionResult.Fail($"Fit failed: {outcome.Error}");

        var fit = outcome.Result;
        fit.Remove("ok");
        var evidence = await support.RecordAsync(study, request, EvidenceKinds.Model,
            $"{method} of {target} on {dataset.Name} v{dataset.Version}" + (features.Count > 0 ? $" with {string.Join(", ", features)}" : string.Empty),
            new { method, dataset = dataset.Name, version = dataset.Version, target, features, options = modelOptions, fit },
            StudyRefs.DatasetKey(dataset.Name));
        var model = new StudyModel
        {
            StudyId = study.StudyId,
            RunId = request.TaskId,
            AgentId = request.AgentId,
            HypothesisId = hypothesisId.Length > 0 ? hypothesisId : null,
            Method = method,
            DatasetId = dataset.DatasetId,
            DatasetName = dataset.Name,
            DatasetVersion = dataset.Version,
            Target = target,
            Features = features,
            OptionsJson = modelOptions.ToJsonString(),
            ResultJson = fit.ToJsonString(),
            EvidenceId = evidence.EvidenceId
        };
        await support.Store.AddModelAsync(model, request.CancellationToken);
        fit["model_id"] = model.ModelId;
        fit["evidence_id"] = evidence.EvidenceId;
        fit["status"] = "candidate: another agent must review_model it before findings rely on it";
        return ToolExecutionResult.Ok(fit.ToJsonString());
    }
}

/// <summary>Any other analysis, as Python in the sandbox.</summary>
public sealed class RunAnalysisTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "run_analysis",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Run Python in the analysis sandbox (no network) for anything fit_model doesn't cover: descriptive statistics, " +
                      "correlations, tests (scipy.stats), calculus (sympy), differential equations (scipy.integrate), charts. Available: " +
                      "pd, np, sm, smf, scipy, stats, sympy, plt; load('table') returns a dataset's training rows as a DataFrame, " +
                      "sql('SELECT ...') queries them. print() what matters and/or set `result` to a JSON-able value; open matplotlib " +
                      "figures are saved as charts. Returns an evidence_id to cite.",
        JsonSchema = """
        { "type": "object", "properties": {
            "purpose": { "type": "string", "description": "What this analysis establishes, in one sentence" },
            "code": { "type": "string" },
            "datasets": { "type": "array", "items": { "type": "string" }, "description": "Tables to load; default all" } },
          "required": ["purpose", "code"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        var code = args is null ? string.Empty : StudyToolSupport.Str(args, "code");
        var purpose = args is null ? string.Empty : StudyToolSupport.Str(args, "purpose");
        if (code.Length == 0 || purpose.Length == 0) return ToolExecutionResult.Fail("purpose and code are required.");
        if (code.Length > 20_000) return ToolExecutionResult.Fail("Keep the code under 20,000 characters.");
        var (tables, files, datasets) = await support.TablesAsync(study, StudyReportValidator.Strings(args!["datasets"]), request.CancellationToken);

        var outcome = await support.Sandbox.RunAsync(new AnalysisJob
        {
            Job = new JsonObject { ["op"] = "analysis", ["code"] = code, ["tables"] = tables },
            Files = files
        }, request.CancellationToken);
        if (!outcome.Ok) return ToolExecutionResult.Fail($"The analysis failed: {outcome.Error}");

        var evidenceId = StudyIds.Short("ev");
        var charts = new List<string>();
        var dir = StudyPaths.FilesDir(support.Options, study.StudyId);
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in outcome.Files.Where(f => f.Key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = $"{evidenceId}-{name}";
            await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes, request.CancellationToken);
            charts.Add(fileName);
        }

        var used = args!["datasets"] is JsonArray { Count: > 0 } ? datasets : StudyToolSupport.Mentioned(datasets, code);
        var r = outcome.Result;
        var evidence = new StudyEvidence
        {
            EvidenceId = evidenceId,
            StudyId = study.StudyId,
            RunId = request.TaskId,
            AgentId = request.AgentId,
            Kind = EvidenceKinds.Analysis,
            SourceKey = StudyToolSupport.SourceKeysOf(used),
            Summary = purpose.Length > 300 ? purpose[..300] : purpose,
            DetailJson = JsonSerializer.Serialize(new { purpose, code, datasets = used.Select(d => new { d.Name, d.Version }), stdout = r["stdout"]?.ToString(), result = r["result"], charts }, ToolJson.Options)
        };
        await support.Store.AddEvidenceAsync(evidence, request.CancellationToken);
        return StudyToolSupport.Ok(new { evidence_id = evidenceId, stdout = r["stdout"], result = r["result"], charts });
    }
}

/// <summary>Accepts or rejects another agent's model.</summary>
public sealed class ReviewModelTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "review_model",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Review a candidate model fitted by ANOTHER agent (you can't review your own): check the diagnostics and warnings, " +
                      "leakage (a feature that encodes the target), overfitting (cross-validation vs fit), violated assumptions, and " +
                      "whether the claim it supports is causal or only correlational. verdict is accept or reject, with notes.",
        JsonSchema = """
        { "type": "object", "properties": {
            "model_id": { "type": "string" },
            "verdict": { "type": "string", "enum": ["accept", "reject"] },
            "notes": { "type": "string" } },
          "required": ["model_id", "verdict", "notes"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        if (args is null) return ToolExecutionResult.Fail("Arguments must be a JSON object.");
        var model = await support.Store.GetModelAsync(study.StudyId, StudyToolSupport.Str(args, "model_id"), request.CancellationToken);
        if (model is null) return ToolExecutionResult.Fail("No such model in this study.");
        if (model.AgentId == request.AgentId)
        {
            return ToolExecutionResult.Fail("You fitted this model, so you can't review it. Ask another agent (spawn a reviewer, or send_message to one).");
        }

        var verdict = StudyToolSupport.Str(args, "verdict").ToLowerInvariant();
        if (verdict is not ("accept" or "reject")) return ToolExecutionResult.Fail("verdict is accept or reject.");
        var notes = StudyToolSupport.Str(args, "notes");
        if (notes.Length == 0) return ToolExecutionResult.Fail("notes are required: say what you checked.");
        var reviewed = model with
        {
            Status = verdict == "accept" ? "accepted" : "rejected",
            ReviewerAgentId = request.AgentId,
            ReviewNotes = notes.Length > 2000 ? notes[..2000] : notes,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        await support.Store.UpdateModelAsync(reviewed, request.CancellationToken);
        return StudyToolSupport.Ok(new { model_id = model.ModelId, status = reviewed.Status });
    }
}

/// <summary>Scores an accepted model on the sealed holdout.</summary>
public sealed class EvaluateOnHoldoutTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "evaluate_on_holdout",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Score an ACCEPTED model on its dataset's sealed holdout rows (never seen by any agent): RMSE/MAE/R² for linear, " +
                      "AUC/accuracy/log loss for logistic, forecast error for ARIMA. Limited per study, so use it on your best " +
                      "model(s), not to search. Returns an evidence_id.",
        JsonSchema = """{ "type": "object", "properties": { "model_id": { "type": "string" } }, "required": ["model_id"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        var ct = request.CancellationToken;
        var model = args is null ? null : await support.Store.GetModelAsync(study.StudyId, StudyToolSupport.Str(args, "model_id"), ct);
        if (model is null) return ToolExecutionResult.Fail("No such model in this study.");
        if (model.HoldoutJson is not null)
        {
            return StudyToolSupport.Ok(new { model_id = model.ModelId, evidence_id = model.HoldoutEvidenceId, holdout = JsonNode.Parse(model.HoldoutJson), note = "Already evaluated." });
        }

        if (model.Status != "accepted") return ToolExecutionResult.Fail($"The model is {model.Status}: only a model another agent accepted can be scored on the holdout.");
        var used = await support.Store.CountHoldoutEvaluationsAsync(study.StudyId, ct);
        if (used >= support.Options.MaxHoldoutEvaluations)
        {
            return ToolExecutionResult.Fail($"This study has used all {support.Options.MaxHoldoutEvaluations} holdout evaluations.");
        }

        var dataset = (await support.Store.ListDatasetsAsync(study.StudyId, currentOnly: false, ct)).FirstOrDefault(d => d.DatasetId == model.DatasetId);
        if (dataset is null || dataset.HoldoutRows == 0) return ToolExecutionResult.Fail("The model's dataset has no sealed holdout.");

        var outcome = await support.Sandbox.RunAsync(new AnalysisJob
        {
            Job = new JsonObject
            {
                ["op"] = "holdout_eval", ["method"] = model.Method, ["target"] = model.Target,
                ["features"] = new JsonArray(model.Features.Select(f => (JsonNode)f!).ToArray()),
                ["options"] = JsonNode.Parse(model.OptionsJson), ["train"] = "data/train.parquet", ["holdout"] = "data/holdout.parquet"
            },
            Files = new Dictionary<string, string>
            {
                ["data/train.parquet"] = StudyPaths.TrainFile(support.Options, dataset),
                ["data/holdout.parquet"] = StudyPaths.HoldoutFile(support.Options, dataset)
            }
        }, ct);
        if (!outcome.Ok) return ToolExecutionResult.Fail($"Holdout evaluation failed: {outcome.Error}");

        var scores = outcome.Result;
        scores.Remove("ok");
        var evidence = await support.RecordAsync(study, request, EvidenceKinds.Holdout,
            $"Holdout score of {model.ModelId} ({model.Method} of {model.Target})", new { model_id = model.ModelId, scores },
            StudyRefs.DatasetKey(dataset.Name));
        await support.Store.UpdateModelAsync(model with { HoldoutJson = scores.ToJsonString(), HoldoutEvidenceId = evidence.EvidenceId }, ct);
        return StudyToolSupport.Ok(new
        {
            model_id = model.ModelId, evidence_id = evidence.EvidenceId, holdout = scores,
            holdout_evaluations_left = support.Options.MaxHoldoutEvaluations - used - 1
        });
    }
}

/// <summary>The data-use plan: what each source is for.</summary>
public sealed class SetSourceRoleTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "set_source_role",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Say what a source is for in this study (study_sources lists them): model_input, calibration (real rates a simulation " +
                      "is checked against), population (who a simulated population consists of), scenario (facts the scenario uses), " +
                      "validation, or not_relevant (reason required). Every source needs a role before the report is accepted.",
        JsonSchema = """
        { "type": "object", "properties": {
            "source": { "type": "string", "description": "e.g. dataset:renewals, document:market-report.pdf, document:facts, connection:crm" },
            "role": { "type": "string", "enum": ["model_input", "calibration", "population", "scenario", "validation", "not_relevant"] },
            "reason": { "type": "string" } },
          "required": ["source", "role"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        if (args is null) return ToolExecutionResult.Fail("Arguments must be a JSON object.");
        var view = await support.Sources.GetAsync(study, request.CancellationToken);
        var source = StudyToolSupport.Str(args, "source");
        if (!view.SourceKeys.Contains(source))
        {
            return ToolExecutionResult.Fail($"No source '{source}'. Sources: {string.Join(", ", view.SourceKeys)}.");
        }

        if (StudyRefs.ParseRole(StudyToolSupport.Str(args, "role")) is not { } role) return ToolExecutionResult.Fail("Unknown role.");
        var reason = StudyToolSupport.Str(args, "reason");
        if (role == SourceRole.NotRelevant && reason.Length == 0) return ToolExecutionResult.Fail("Say why the source isn't relevant (reason).");
        await support.Store.SetSourceRoleAsync(new SourceRoleEntry
        {
            StudyId = study.StudyId,
            SourceKey = source,
            Role = role,
            Reason = reason.Length > 0 ? reason : null,
            AgentId = request.AgentId
        }, request.CancellationToken);
        return StudyToolSupport.Ok(new { source, role = StudyRefs.RoleName(role), sources_without_a_role = view.Unassigned.Where(k => k != source) });
    }
}

/// <summary>A simulated experiment whose decisions become a dataset.</summary>
public sealed class RunSimulationTool(StudyToolSupport support, ExperimentRunner runner, IGrainFactory grains) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "run_simulation",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Run an experiment with a simulated population built from the study's data: segments with shares and attributes " +
                      "(cite the evidence they come from), a control and up to two treatment scenarios, a decision each participant " +
                      "makes, rounds with optional word of mouth, repeated runs, and optionally the real rate of one choice to calibrate " +
                      "against. Every decision is saved as a new dataset you can query and model. Simulated people are not real " +
                      "people: treat the results as directional, and say so in the report.",
        JsonSchema = """
        { "type": "object", "properties": {
            "name": { "type": "string", "description": "Short name, e.g. rent-increase-8pct" },
            "population": { "type": "object", "properties": {
                "size": { "type": "integer", "description": "Participants (capped)" },
                "evidence": { "type": "array", "items": { "type": "string" }, "description": "Evidence ids the segments and shares come from" },
                "segments": { "type": "array", "items": { "type": "object", "properties": {
                    "name": { "type": "string" }, "share": { "type": "number" },
                    "description": { "type": "string", "description": "Who these people are, in second person ('You rent a studio...')" },
                    "attributes": { "type": "object", "additionalProperties": { "type": "string" } },
                    "ranges": { "type": "object", "additionalProperties": { "type": "array", "items": { "type": "number" } }, "description": "Numeric attributes drawn per participant: {\"income_k\": [30, 60]}" } },
                  "required": ["name", "share", "description"] } } },
              "required": ["segments", "evidence"] },
            "conditions": { "type": "array", "items": { "type": "object", "properties": { "name": { "type": "string" }, "scenario": { "type": "string" } }, "required": ["scenario"] }, "description": "First is the control" },
            "decision": { "type": "object", "properties": {
                "question": { "type": "string" },
                "options": { "type": "array", "items": { "type": "string" } },
                "value": { "type": "object", "properties": { "name": { "type": "string" }, "min": { "type": "number" }, "max": { "type": "number" } } } },
              "required": ["question", "options"] },
            "facts": { "type": "array", "items": { "type": "object", "properties": { "fact": { "type": "string" }, "evidence_id": { "type": "string" } }, "required": ["fact"] } },
            "rounds": { "type": "integer" }, "word_of_mouth": { "type": "boolean" }, "replications": { "type": "integer" },
            "calibration": { "type": "object", "properties": { "option": { "type": "string" }, "rate": { "type": "number" }, "evidence_id": { "type": "string" } } },
            "seed": { "type": "integer" } },
          "required": ["name", "population", "conditions", "decision"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        if (args is null) return ToolExecutionResult.Fail("Arguments must be a JSON object.");
        var ct = request.CancellationToken;
        var (spec, errors, notes) = ExperimentRunner.Parse(args, support.Options);
        if (spec is null) return ToolExecutionResult.Fail(string.Join(" ", errors));

        // The population and facts must come from this study's evidence.
        var cited = spec.CitedEvidence.ToList();
        var known = (await support.Store.GetEvidenceAsync(study.StudyId, cited, ct)).ToDictionary(e => e.EvidenceId);
        var missing = cited.Where(id => !known.ContainsKey(id)).ToList();
        if (missing.Count > 0) return ToolExecutionResult.Fail($"These evidence ids don't exist in this study: {string.Join(", ", missing)}.");

        var budget = await grains.GetGrain<IWorkspaceGrain>(study.WorkspaceId).CheckBudget();
        if (!budget.Allowed) return ToolExecutionResult.Fail($"The study's daily budget doesn't allow a simulation now: {budget.Reason}");

        var outcome = await runner.RunAsync(spec, study.TenantId, request.TaskId, study.WorkspaceId, request.AgentId, ct);
        // Charged like any model call: to the study's daily budget and the organization's plan.
        await grains.GetGrain<IWorkspaceGrain>(study.WorkspaceId).RecordUsage(request.AgentId, outcome.Tokens, outcome.CostUsd);
        await grains.GetGrain<Tenancy.ITenantGrain>(Tenancy.TenantIds.Normalize(study.TenantId)).RecordUsage(new Tenancy.UsageDelta
        {
            Tokens = outcome.Tokens, CostUsd = outcome.CostUsd, LlmCalls = outcome.Calls
        });
        if (outcome.Rows.Count == 0) return ToolExecutionResult.Fail("No participant made a decision; the simulation produced no data.");

        // The decisions become a dataset like any other (no holdout: it's the experiment's output).
        var existing = await support.Store.ListDatasetsAsync(study.StudyId, currentOnly: false, ct);
        var tableName = UniqueTable(existing, "sim_" + ExperimentRunner.ColumnName(spec.Name));
        var dataset = await StudyIngest.IngestRowsAsync(support, study, tableName, $"{spec.Name} (simulated)", outcome.Rows, ct);
        if (dataset is null) return ToolExecutionResult.Fail("The simulation ran, but its decisions couldn't be saved as a dataset.");

        var sources = known.Values.Select(e => e.SourceKey).OfType<string>().SelectMany(k => k.Split(',')).Append(StudyRefs.DatasetKey(dataset.Name)).Distinct();
        var evidence = await support.RecordAsync(study, request, EvidenceKinds.Simulation,
            $"Simulation '{spec.Name}': {outcome.Participants} participants, {outcome.Rows.Count} decisions",
            new { spec = args, summary = outcome.Summary, dataset = dataset.Name, notes }, string.Join(",", sources));
        await support.Store.AddSimulationAsync(new StudySimulation
        {
            SimulationId = StudyIds.Short("sim"),
            StudyId = study.StudyId,
            RunId = request.TaskId,
            AgentId = request.AgentId,
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
        }, ct);

        return StudyToolSupport.Ok(new
        {
            evidence_id = evidence.EvidenceId,
            dataset = dataset.Name,
            summary = outcome.Summary,
            notes,
            reminder = "Simulated people are not real: label findings from this as simulated and directional."
        });
    }

    private static string UniqueTable(IReadOnlyList<StudyDataset> existing, string name)
    {
        name = name.Length > 40 ? name[..40].TrimEnd('_') : name;
        var candidate = name;
        for (var i = 2; existing.Any(d => d.Name == candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }
}

/// <summary>The study report, checked by the runtime before the run can finish.</summary>
public sealed class SubmitReportTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "submit_report",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Submit the study report. Each finding states a claim and cites the evidence ids (and model ids) behind it; a finding " +
                      "without evidence is shown as interpretation. The runtime refuses the report if a cited id doesn't exist, a cited " +
                      "model wasn't accepted in review, or a source has no role in the data-use plan. Call complete_task afterwards.",
        JsonSchema = """
        { "type": "object", "properties": {
            "summary": { "type": "string", "description": "The answer to the study's question, in Markdown" },
            "findings": { "type": "array", "items": { "type": "object", "properties": {
                "claim": { "type": "string" },
                "evidence": { "type": "array", "items": { "type": "string" } },
                "models": { "type": "array", "items": { "type": "string" } },
                "confidence": { "type": "string", "enum": ["high", "medium", "low"] } },
              "required": ["claim", "evidence"] } },
            "limitations": { "type": "array", "items": { "type": "string" } },
            "open_questions": { "type": "array", "items": { "type": "string" } },
            "recommendations": { "type": "array", "items": { "type": "string" } } },
          "required": ["summary", "findings", "limitations"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var report = StudyToolSupport.Args(request);
        if (report is null) return ToolExecutionResult.Fail("The report must be a JSON object.");
        var ct = request.CancellationToken;
        var findings = report["findings"] as JsonArray ?? [];
        var evidenceIds = findings.OfType<JsonObject>().SelectMany(f => StudyReportValidator.Strings(f["evidence"])).Distinct().ToList();
        var models = (await support.Store.ListModelsAsync(study.StudyId, ct)).ToDictionary(m => m.ModelId);
        evidenceIds.AddRange(models.Values.Select(m => m.EvidenceId));
        var evidence = (await support.Store.GetEvidenceAsync(study.StudyId, evidenceIds.Distinct().ToList(), ct)).ToDictionary(e => e.EvidenceId);
        var view = await support.Sources.GetAsync(study, ct);

        var result = StudyReportValidator.Validate(new StudyReportValidator.Input(report, evidence, models, view.SourceKeys.ToHashSet(), view.Roles));
        if (!result.Ok) return ToolExecutionResult.Fail("The report wasn't accepted: " + string.Join(" ", result.Errors));

        var checkedReport = result.Report;
        var counts = await support.Store.CountEvidenceBySourceAsync(study.StudyId, ct);
        checkedReport["data_coverage"] = new JsonArray(view.SourceKeys.Select(k => (JsonNode)new JsonObject
        {
            ["source"] = k,
            ["role"] = view.Roles.TryGetValue(k, out var r) ? StudyRefs.RoleName(r.Role) : "unassigned",
            ["reason"] = view.Roles.TryGetValue(k, out var rr) ? rr.Reason : null,
            ["evidence_count"] = counts.GetValueOrDefault(k)
        }).ToArray());
        checkedReport["warnings"] = new JsonArray(result.Warnings.Select(w => (JsonNode)w!).ToArray());
        await support.Store.SaveReportAsync(new StudyReport { RunId = request.TaskId, StudyId = study.StudyId, AgentId = request.AgentId, Json = checkedReport.ToJsonString() }, ct);
        return StudyToolSupport.Ok(new { accepted = true, warnings = result.Warnings, next = "Call complete_task with the report's summary." });
    }
}
