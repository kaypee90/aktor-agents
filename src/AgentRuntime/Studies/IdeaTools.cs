using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.Tools;

namespace AgentRuntime.Studies;

/// <summary>
/// Runs and records financial projections (docs/studies.md, "Evaluating an idea"), for an agent's
/// project_financials and for a person's what-if on the study page alike: resolves the drivers
/// (from the assumption register or as given), runs <see cref="BusinessModelSimulator"/>, and saves
/// the result as evidence and as a projection.
/// </summary>
public sealed class StudyProjections(StudyToolSupport support)
{
    public sealed record Outcome(StudyProjection? Projection, JsonObject? Result, IReadOnlyList<string> Errors);

    public async Task<Outcome> RunAsync(StudyInfo study, JsonObject args, string? runId, string agentId, CancellationToken ct)
    {
        var name = StudyToolSupport.Str(args, "name");
        if (name.Length == 0) return new Outcome(null, null, ["name is required, e.g. base-case."]);
        if (name.Length > 80) name = name[..80];
        var revenueModel = StudyToolSupport.Str(args, "revenue_model") is { Length: > 0 } m ? m.ToLowerInvariant() : "subscription";
        if (!BusinessModelSimulator.RevenueModels.Contains(revenueModel))
        {
            return new Outcome(null, null, [$"revenue_model is {string.Join(", ", BusinessModelSimulator.RevenueModels)}."]);
        }

        var horizon = Int(args["horizon_months"]) ?? study.Idea?.HorizonMonths ?? 36;
        if (horizon is < 6 or > BusinessModelSimulator.MaxHorizonMonths)
        {
            return new Outcome(null, null, [$"horizon_months is between 6 and {BusinessModelSimulator.MaxHorizonMonths}."]);
        }

        var all = (await support.Store.ListAssumptionsAsync(study.StudyId, currentOnly: false, ct)).ToDictionary(a => a.AssumptionId);
        var (drivers, errors) = BusinessModelSimulator.ParseDrivers(args["drivers"] as JsonObject, all, revenueModel);
        if (drivers is null) return new Outcome(null, null, errors);

        var spec = new ProjectionSpec
        {
            Name = name,
            RevenueModel = revenueModel,
            HorizonMonths = horizon,
            Iterations = Math.Clamp(Int(args["iterations"]) ?? 2000, 100, BusinessModelSimulator.MaxIterations),
            Seed = Int(args["seed"]) ?? 7,
            Currency = study.Idea?.Currency ?? "USD",
            Drivers = drivers
        };
        var result = BusinessModelSimulator.Run(spec);

        // Where each driver came from, so a projection can be traced and challenged.
        var inputs = new JsonObject();
        var unsupported = new List<string>();
        foreach (var d in drivers.Values.OrderBy(d => BusinessModelSimulator.Catalog.ToList().FindIndex(i => i.Name == d.Name)))
        {
            var assumption = d.AssumptionId is { } id ? all.GetValueOrDefault(id) : null;
            if (d.Source == Driver.Given || assumption is { Supported: false }) unsupported.Add(d.Name);
            inputs[d.Name] = new JsonObject
            {
                ["low"] = d.Low,
                ["likely"] = d.Likely,
                ["high"] = d.High,
                ["source"] = d.Source,
                ["assumption_id"] = d.AssumptionId,
                ["assumption"] = assumption?.Name,
                ["assumption_status"] = assumption?.Status,
                ["evidence"] = assumption is null ? null : new JsonArray(assumption.Evidence.Select(e => (JsonNode)e!).ToArray())
            };
        }

        result["inputs"] = inputs;
        result["unsupported_drivers"] = new JsonArray(unsupported.Select(u => (JsonNode)u!).ToArray());
        var summary = result["summary"]!;
        var currency = spec.Currency;
        var evidence = await support.RecordAsync(study, runId, agentId, EvidenceKinds.Projection,
            $"Projection {name} ({revenueModel}, {horizon} months): pays back in {Pct(summary["p_payback"])} of {spec.Iterations} simulated futures, " +
            $"median NPV {Money(summary["npv"]?["p50"], currency)}",
            new { spec = new { name, revenue_model = revenueModel, horizon_months = horizon, spec.Iterations, spec.Seed, drivers = inputs }, result },
            sourceKey: null, ct);
        var projection = new StudyProjection
        {
            StudyId = study.StudyId,
            RunId = runId,
            AgentId = agentId,
            Name = name,
            SpecJson = new JsonObject
            {
                ["name"] = name, ["revenue_model"] = revenueModel, ["horizon_months"] = horizon, ["iterations"] = spec.Iterations,
                ["seed"] = spec.Seed, ["drivers"] = args["drivers"]?.DeepClone()
            }.ToJsonString(),
            ResultJson = result.ToJsonString(),
            EvidenceId = evidence.EvidenceId
        };
        await support.Store.AddProjectionAsync(projection, ct);
        return new Outcome(projection, result, []);
    }

    private static int? Int(JsonNode? node) => BusinessModelSimulator.Number(node) is { } d && Math.Abs(d) < int.MaxValue ? (int)Math.Round(d) : null;

    internal static string Pct(JsonNode? share) =>
        BusinessModelSimulator.Number(share) is { } d ? (d * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "?";

    internal static string Money(JsonNode? amount, string currency) =>
        BusinessModelSimulator.Number(amount) is { } d ? $"{d.ToString("#,0", CultureInfo.InvariantCulture)} {currency}" : "?";
}

/// <summary>Records one number an idea's case rests on, as a range with its evidence.</summary>
public sealed partial class RecordAssumptionTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "record_assumption",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Record an assumption the idea's case rests on (market size, adoption, price, churn, cost to acquire a customer, " +
                      "costs, time to build...) as a range: low, likely, high. Cite the evidence ids it comes from (document passages, " +
                      "queries, experiments, connection calls); one without evidence is shown as a guess. Recording the same name again " +
                      "replaces it. Another agent should review_assumption it. project_financials takes assumption ids as drivers.",
        JsonSchema = $$"""
        { "type": "object", "properties": {
            "name": { "type": "string", "description": "Lowercase with underscores; use a driver name when it feeds one: {{string.Join(", ", BusinessModelSimulator.Catalog.Select(d => d.Name))}}" },
            "description": { "type": "string", "description": "What the number means, e.g. 'Independent landlords with 5-50 units in the US'" },
            "category": { "type": "string", "enum": [{{string.Join(", ", AssumptionCategories.All.Select(c => $"\"{c}\""))}}] },
            "unit": { "type": "string", "description": "e.g. customers, USD per month, share (0-1), months" },
            "low": { "type": "number" }, "likely": { "type": "number" }, "high": { "type": "number" },
            "evidence": { "type": "array", "items": { "type": "string" }, "description": "Evidence ids the range comes from" },
            "rationale": { "type": "string", "description": "How the evidence gives this range" } },
          "required": ["name", "likely", "category"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        if (args is null) return ToolExecutionResult.Fail("Arguments must be a JSON object.");
        var ct = request.CancellationToken;
        var name = NameOf(StudyToolSupport.Str(args, "name"));
        if (name.Length == 0) return ToolExecutionResult.Fail("name is required, e.g. monthly_churn.");
        if (Number(args["likely"]) is not { } likely) return ToolExecutionResult.Fail("likely is required: the most likely value.");
        var low = Number(args["low"]) ?? likely;
        var high = Number(args["high"]) ?? likely;
        if (!(low <= likely && likely <= high)) return ToolExecutionResult.Fail($"low ≤ likely ≤ high (got {low}, {likely}, {high}).");
        if (BusinessModelSimulator.Info(name) is { } driver && (low < driver.Min || high > driver.Max))
        {
            return ToolExecutionResult.Fail(driver.Unit == "share"
                ? $"{name} is a share between 0 and 1: give 0.05 for 5%."
                : $"{name} must be between {driver.Min} and {driver.Max}.");
        }

        var cited = StudyReportValidator.Strings(args["evidence"]);
        var known = (await support.Store.GetEvidenceAsync(study.StudyId, cited, ct)).Select(e => e.EvidenceId).ToHashSet();
        var unknown = cited.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0) return ToolExecutionResult.Fail($"These evidence ids don't exist in this study: {string.Join(", ", unknown)}. Cite only ids tools returned.");

        var previous = (await support.Store.ListAssumptionsAsync(study.StudyId, currentOnly: true, ct)).FirstOrDefault(a => a.Name == name);
        var assumption = new StudyAssumption
        {
            StudyId = study.StudyId,
            RunId = request.TaskId,
            AgentId = request.AgentId,
            Name = name,
            Description = Clip(StudyToolSupport.Str(args, "description"), 500),
            Category = AssumptionCategories.Normalize(StudyToolSupport.Str(args, "category")),
            // Money is in the idea's currency unless the agent says otherwise.
            Unit = StudyToolSupport.Str(args, "unit") is { Length: > 0 } u ? Clip(u, 60)
                : BusinessModelSimulator.Info(name)?.Unit is "currency" ? study.Idea?.Currency ?? "USD" : BusinessModelSimulator.Info(name)?.Unit,
            Low = low,
            Likely = likely,
            High = high,
            Evidence = cited,
            Rationale = StudyToolSupport.Str(args, "rationale") is { Length: > 0 } r ? Clip(r, 2000) : null
        };
        await support.Store.AddAssumptionAsync(assumption, ct);
        return StudyToolSupport.Ok(new
        {
            assumption_id = assumption.AssumptionId,
            replaces = previous?.AssumptionId,
            supported = assumption.Supported,
            note = assumption.Supported
                ? "Another agent should review_assumption it."
                : "No evidence cited: it's shown as a guess. Find evidence (search_knowledge, query_dataset, run_simulation) and record it again.",
            use = BusinessModelSimulator.Info(name) is not null ? $"project_financials drivers: {{\"{name}\": \"{assumption.AssumptionId}\"}}" : null
        });
    }

    /// <summary>"Monthly churn %" → "monthly_churn".</summary>
    public static string NameOf(string raw)
    {
        var name = NamePattern().Replace(raw.Trim().ToLowerInvariant(), "_").Trim('_');
        return name.Length > 60 ? name[..60] : name;
    }

    private static double? Number(JsonNode? node) => BusinessModelSimulator.Number(node);

    private static string Clip(string s, int n) => s.Length > n ? s[..n] : s;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NamePattern();
}

/// <summary>Accepts or challenges another agent's assumption.</summary>
public sealed class ReviewAssumptionTool(StudyToolSupport support) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "review_assumption",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Review an assumption recorded by ANOTHER agent (you can't review your own): does its evidence support the range, " +
                      "is the range wide enough for what isn't known, is it optimistic? verdict is accept or challenge, with notes. " +
                      "A challenge can suggest a better range; its author (or anyone) then records it again.",
        JsonSchema = """
        { "type": "object", "properties": {
            "assumption_id": { "type": "string" },
            "verdict": { "type": "string", "enum": ["accept", "challenge"] },
            "notes": { "type": "string" },
            "suggested": { "type": "object", "properties": { "low": { "type": "number" }, "likely": { "type": "number" }, "high": { "type": "number" } } } },
          "required": ["assumption_id", "verdict", "notes"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        if (args is null) return ToolExecutionResult.Fail("Arguments must be a JSON object.");
        var ct = request.CancellationToken;
        var id = StudyToolSupport.Str(args, "assumption_id");
        var assumption = (await support.Store.ListAssumptionsAsync(study.StudyId, currentOnly: false, ct)).FirstOrDefault(a => a.AssumptionId == id);
        if (assumption is null) return ToolExecutionResult.Fail("No such assumption in this study.");
        if (assumption.AgentId == request.AgentId)
        {
            return ToolExecutionResult.Fail("You recorded this assumption, so you can't review it. Ask another agent (spawn a reviewer, or send_message to one).");
        }

        var verdict = StudyToolSupport.Str(args, "verdict").ToLowerInvariant();
        if (verdict is not ("accept" or "challenge")) return ToolExecutionResult.Fail("verdict is accept or challenge.");
        var notes = StudyToolSupport.Str(args, "notes");
        if (notes.Length == 0) return ToolExecutionResult.Fail("notes are required: say what you checked.");
        if (args["suggested"] is JsonObject s && s["likely"] is not null)
        {
            notes += $" Suggested range: {s["low"] ?? s["likely"]} – {s["likely"]} – {s["high"] ?? s["likely"]}.";
        }

        var reviewed = assumption with
        {
            Status = verdict == "accept" ? "accepted" : "challenged",
            ReviewerAgentId = request.AgentId,
            ReviewNotes = notes.Length > 2000 ? notes[..2000] : notes,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        await support.Store.UpdateAssumptionAsync(reviewed, ct);
        return StudyToolSupport.Ok(new { assumption_id = id, status = reviewed.Status, current = assumption.Current });
    }
}

/// <summary>Simulates the idea's business model many times over the horizon.</summary>
public sealed class ProjectFinancialsTool(StudyToolSupport support, StudyProjections projections) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "project_financials",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Project the idea's finances: the runtime simulates the business month by month over the horizon, thousands of times, " +
                      "drawing each uncertain driver from its range. Returns the chance it pays back, NPV, revenue and profit by year, the " +
                      "funding it needs, unit economics (LTV, CAC, payback) and which drivers move the result most, with an evidence_id. " +
                      "Drivers are assumption ids (preferred: they carry evidence), plain numbers, or {low, likely, high}. Drivers: " +
                      string.Join("; ", BusinessModelSimulator.Catalog.Select(d => $"{d.Name} ({d.Description}{(d.Default is { } x ? $", default {x.ToString(CultureInfo.InvariantCulture)}" : ", required")})")) +
                      ". cac is required when marketing_spend is given. Run scenarios (base, pessimistic, a different price) as separate projections.",
        JsonSchema = """
        { "type": "object", "properties": {
            "name": { "type": "string", "description": "e.g. base-case, price-49, no-paid-marketing" },
            "revenue_model": { "type": "string", "enum": ["subscription", "one_time", "usage"] },
            "drivers": { "type": "object", "additionalProperties": {}, "description": "driver name → assumption id ('asm-…'), number, or {low, likely, high}" },
            "horizon_months": { "type": "integer", "description": "Default: the idea's horizon" },
            "iterations": { "type": "integer", "description": "Simulated futures, default 2000" },
            "seed": { "type": "integer" } },
          "required": ["name", "drivers"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        if (await support.StudyOfAsync(request) is not { } study) return StudyToolSupport.NotInStudy();
        var args = StudyToolSupport.Args(request);
        if (args is null) return ToolExecutionResult.Fail("Arguments must be a JSON object.");
        var outcome = await projections.RunAsync(study, args, request.TaskId, request.AgentId, request.CancellationToken);
        if (outcome.Projection is null) return ToolExecutionResult.Fail(string.Join(" ", outcome.Errors));

        var r = outcome.Result!;
        var sensitivity = r["sensitivity"]!.DeepClone().AsObject();
        if (sensitivity["drivers"] is JsonArray rows) sensitivity["drivers"] = new JsonArray(rows.Take(6).Select(x => x!.DeepClone()).ToArray());
        return StudyToolSupport.Ok(new
        {
            evidence_id = outcome.Projection.EvidenceId,
            projection_id = outcome.Projection.ProjectionId,
            currency = r["currency"],
            summary = r["summary"],
            unit_economics = r["unit_economics"],
            years = r["years"],
            sensitivity,
            warnings = r["warnings"],
            unsupported_drivers = r["unsupported_drivers"],
            reminder = "These numbers are only as good as the drivers: cite this evidence_id for profitability, and name the assumptions it rests on."
        });
    }
}
