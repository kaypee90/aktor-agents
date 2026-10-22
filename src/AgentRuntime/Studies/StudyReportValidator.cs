using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentRuntime.Studies;

/// <summary>
/// Checks a study report before the run can finish (docs/studies.md, "What the runtime enforces"):
/// every cited evidence id and model exists in this study, every model a finding relies on was
/// accepted by an agent other than its author, and every source has a role in the data-use plan.
/// Findings without evidence are kept but marked as interpretation, and findings resting on
/// simulated evidence are marked as simulated. An idea study's report also carries a verdict
/// (feasibility, profitability, usefulness and a recommendation) whose scores must rest on evidence,
/// profitability on a financial projection, and must not contradict what the projection found.
/// </summary>
public static class StudyReportValidator
{
    public sealed record Input(
        JsonObject Report,
        IReadOnlyDictionary<string, StudyEvidence> Evidence,
        IReadOnlyDictionary<string, StudyModel> Models,
        IReadOnlyCollection<string> Sources,
        IReadOnlyDictionary<string, SourceRoleEntry> Roles,
        string Kind = StudyKinds.Question,
        IReadOnlyDictionary<string, StudyAssumption>? Assumptions = null,
        IReadOnlyDictionary<string, StudyProjection>? ProjectionsByEvidence = null);

    public sealed record Result(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, JsonObject Report)
    {
        public bool Ok => Errors.Count == 0;
    }

    public static readonly string[] Confidences = ["high", "medium", "low"];
    public static readonly string[] Recommendations = ["go", "go_with_changes", "no_go", "needs_more_evidence"];
    public static readonly string[] Dimensions = ["feasibility", "profitability", "usefulness"];

    public static Result Validate(Input input)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var report = (JsonObject)input.Report.DeepClone();

        if (string.IsNullOrWhiteSpace(Str(report, "summary"))) errors.Add("summary is required.");

        var findings = report["findings"] as JsonArray ?? [];
        if (findings.Count == 0) errors.Add("Add at least one finding.");

        var unknownEvidence = new SortedSet<string>();
        var unknownModels = new SortedSet<string>();
        var unreviewed = new SortedSet<string>();
        var interpretations = 0;
        foreach (var node in findings)
        {
            if (node is not JsonObject finding)
            {
                errors.Add("Each finding is an object with a claim.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(Str(finding, "claim"))) errors.Add("Every finding needs a claim.");
            var evidenceIds = Strings(finding["evidence"]);
            var modelIds = Strings(finding["models"]);
            unknownEvidence.UnionWith(evidenceIds.Where(id => !input.Evidence.ContainsKey(id)));
            unknownModels.UnionWith(modelIds.Where(id => !input.Models.ContainsKey(id)));
            foreach (var id in modelIds.Where(input.Models.ContainsKey))
            {
                if (input.Models[id].Status != "accepted") unreviewed.Add($"{id} ({input.Models[id].Status})");
            }

            var known = evidenceIds.Where(input.Evidence.ContainsKey).Select(id => input.Evidence[id]).ToList();
            // A model's own fit is evidence too.
            known.AddRange(modelIds.Where(input.Models.ContainsKey)
                .Select(id => input.Evidence.GetValueOrDefault(input.Models[id].EvidenceId)).OfType<StudyEvidence>());
            var supported = known.Count > 0;
            if (!supported) interpretations++;
            finding["status"] = supported ? "supported" : "interpretation";
            finding["simulated"] = known.Any(e => e.Kind == EvidenceKinds.Simulation || (e.SourceKey ?? string.Empty).Contains("dataset:sim_"));
            finding["projected"] = known.Any(e => e.Kind == EvidenceKinds.Projection);
            finding["evidence_kinds"] = new JsonArray(known.Select(e => e.Kind).Distinct().Order().Select(k => (JsonNode)k!).ToArray());
            var confidence = Str(finding, "confidence")?.ToLowerInvariant();
            finding["confidence"] = Confidences.Contains(confidence) ? confidence : supported ? "medium" : "low";
        }

        if (report["verdict"] is JsonObject verdict) CheckVerdict(input, verdict, unknownEvidence, errors, warnings);
        else if (report["verdict"] is not null) errors.Add("verdict is an object.");
        else if (input.Kind == StudyKinds.Idea)
        {
            errors.Add("An idea study's report needs a verdict: a recommendation and feasibility, profitability and usefulness scores, each citing evidence.");
        }

        if (unknownEvidence.Count > 0)
        {
            errors.Add($"These evidence ids don't exist in this study: {string.Join(", ", unknownEvidence)}. " +
                       "Cite only ids that tools returned to you.");
        }

        if (unknownModels.Count > 0) errors.Add($"These model ids don't exist in this study: {string.Join(", ", unknownModels)}.");
        if (unreviewed.Count > 0)
        {
            errors.Add($"Findings rely on models that weren't accepted in review: {string.Join(", ", unreviewed)}. " +
                       "Another agent must accept a model (review_model) before a finding can rest on it.");
        }

        var unassigned = input.Sources.Where(s => !input.Roles.TryGetValue(s, out var r) || r.Role == SourceRole.Unassigned).Order().ToList();
        if (unassigned.Count > 0)
        {
            errors.Add($"Every source needs a role in the data-use plan (set_source_role). Missing: {string.Join(", ", unassigned)}.");
        }

        var unexplained = input.Roles.Values
            .Where(r => input.Sources.Contains(r.SourceKey) && r.Role == SourceRole.NotRelevant && string.IsNullOrWhiteSpace(r.Reason))
            .Select(r => r.SourceKey).Order().ToList();
        if (unexplained.Count > 0) errors.Add($"Sources marked not relevant need a reason: {string.Join(", ", unexplained)}.");

        if (interpretations > 0)
        {
            warnings.Add($"{interpretations} finding(s) cite no evidence: they're shown as interpretation, not as results.");
        }

        if (Strings(report["limitations"]).Count == 0) warnings.Add("No limitations listed: every study has some.");

        report["checked_at"] = DateTimeOffset.UtcNow.ToString("O");
        return new Result(errors, warnings, report);
    }

    /// <summary>The verdict on an idea: every score cites evidence, profitability cites a projection
    /// and agrees with it, key assumptions exist. The runtime adds the overall score, the cited
    /// projection's key numbers, and how well the assumption register is supported.</summary>
    private static void CheckVerdict(Input input, JsonObject verdict, SortedSet<string> unknownEvidence, List<string> errors, List<string> warnings)
    {
        var recommendation = Str(verdict, "recommendation")?.ToLowerInvariant();
        if (!Recommendations.Contains(recommendation)) errors.Add($"verdict.recommendation is one of {string.Join(", ", Recommendations)}.");
        else verdict["recommendation"] = recommendation;

        var projections = input.ProjectionsByEvidence ?? new Dictionary<string, StudyProjection>();
        var scores = new Dictionary<string, double>();
        foreach (var dimension in Dimensions)
        {
            if (verdict[dimension] is not JsonObject d)
            {
                errors.Add($"verdict.{dimension} is required: {{score 0-10, summary, evidence}}.");
                continue;
            }

            if (BusinessModelSimulator.Number(d["score"]) is not { } score || score is < 0 or > 10)
            {
                errors.Add($"verdict.{dimension}.score is a number from 0 to 10.");
                continue;
            }

            scores[dimension] = score;
            if (string.IsNullOrWhiteSpace(Str(d, "summary"))) errors.Add($"verdict.{dimension}.summary says why it scores {score}.");
            var ids = Strings(d["evidence"]);
            unknownEvidence.UnionWith(ids.Where(id => !input.Evidence.ContainsKey(id)));
            var known = ids.Where(input.Evidence.ContainsKey).Select(id => input.Evidence[id]).ToList();
            if (known.Count == 0)
            {
                errors.Add($"verdict.{dimension} cites no evidence: a score needs the evidence ids it rests on.");
                continue;
            }

            d["evidence_kinds"] = new JsonArray(known.Select(e => e.Kind).Distinct().Order().Select(k => (JsonNode)k!).ToArray());
            switch (dimension)
            {
                case "profitability":
                    var cited = ids.Where(projections.ContainsKey).Select(id => projections[id]).ToList();
                    if (cited.Count == 0)
                    {
                        errors.Add("verdict.profitability must cite a financial projection (project_financials' evidence_id): profitability is computed, not estimated.");
                        break;
                    }

                    var payback = cited.Select(p => PaybackShare(p)).OfType<double>().ToList();
                    if (payback.Count > 0 && score >= 7 && payback.Max() < 0.4)
                    {
                        errors.Add($"verdict.profitability scores {score}, but the cited projection(s) pay back in at most {payback.Max():P0} of simulated futures. " +
                                   "Lower the score, or cite a projection that supports it.");
                    }
                    else if (payback.Count > 0 && score <= 3 && payback.Min() >= 0.8)
                    {
                        warnings.Add($"verdict.profitability scores {score}, though the cited projection(s) pay back in at least {payback.Min():P0} of simulated futures.");
                    }

                    // The first projection cited is the lead case (the base case); others are scenarios.
                    verdict["projection"] = KeyNumbers(cited[0]);
                    var weak = cited.SelectMany(UnsupportedDrivers).Distinct().ToList();
                    if (weak.Count > 0) warnings.Add($"The cited projection rests on drivers without evidence: {string.Join(", ", weak)}.");
                    break;
                case "usefulness" when !known.Any(e => e.Kind is EvidenceKinds.Simulation or EvidenceKinds.Passage or EvidenceKinds.Connection or EvidenceKinds.Query):
                    warnings.Add("verdict.usefulness cites no customer evidence (an experiment, documents, data or connections).");
                    break;
            }
        }

        if (scores.Count == Dimensions.Length)
        {
            verdict["overall"] = Math.Round(scores.Values.Average(), 1);
            if (recommendation == "go" && scores.Values.Min() < 4) warnings.Add("The recommendation is go, though one dimension scores below 4.");
            if (recommendation == "no_go" && scores.Values.Min() >= 7) warnings.Add("The recommendation is no_go, though every dimension scores 7 or more.");
        }

        var assumptions = input.Assumptions ?? new Dictionary<string, StudyAssumption>();
        var keys = Strings(verdict["key_assumptions"]);
        var unknownAssumptions = keys.Where(k => !assumptions.ContainsKey(k)).ToList();
        if (unknownAssumptions.Count > 0) errors.Add($"These assumption ids don't exist in this study: {string.Join(", ", unknownAssumptions)}.");
        var keyed = keys.Where(assumptions.ContainsKey).Select(k => assumptions[k]).ToList();
        if (keyed.Any(a => a.Status == "challenged")) warnings.Add($"Key assumptions were challenged in review: {string.Join(", ", keyed.Where(a => a.Status == "challenged").Select(a => a.Name))}.");
        if (keyed.Any(a => !a.Supported)) warnings.Add($"Key assumptions cite no evidence: {string.Join(", ", keyed.Where(a => !a.Supported).Select(a => a.Name))}.");

        var current = assumptions.Values.Where(a => a.Current).ToList();
        verdict["assumption_check"] = new JsonObject
        {
            ["total"] = current.Count,
            ["with_evidence"] = current.Count(a => a.Supported),
            ["accepted"] = current.Count(a => a.Status == "accepted"),
            ["challenged"] = current.Count(a => a.Status == "challenged"),
            ["unreviewed"] = current.Count(a => a.Status == "candidate"),
            ["guesses"] = new JsonArray(current.Where(a => !a.Supported).Select(a => (JsonNode)a.Name!).ToArray())
        };
        if (current.Count > 0 && current.All(a => a.Status == "candidate")) warnings.Add("No assumption was reviewed by another agent.");
    }

    private static double? PaybackShare(StudyProjection p)
    {
        try
        {
            return BusinessModelSimulator.Number(JsonNode.Parse(p.ResultJson)?["summary"]?["p_payback"]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> UnsupportedDrivers(StudyProjection p)
    {
        try
        {
            return Strings(JsonNode.Parse(p.ResultJson)?["unsupported_drivers"]);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A projection's headline numbers, kept in the report so it reads on its own.</summary>
    private static JsonObject KeyNumbers(StudyProjection p)
    {
        JsonNode? r;
        try { r = JsonNode.Parse(p.ResultJson); } catch (JsonException) { r = null; }
        var summary = r?["summary"];
        return new JsonObject
        {
            ["projection_id"] = p.ProjectionId,
            ["evidence_id"] = p.EvidenceId,
            ["name"] = p.Name,
            ["currency"] = r?["currency"]?.DeepClone(),
            ["horizon_months"] = r?["horizon_months"]?.DeepClone(),
            ["p_payback"] = summary?["p_payback"]?.DeepClone(),
            ["p_npv_positive"] = summary?["p_npv_positive"]?.DeepClone(),
            ["npv"] = summary?["npv"]?.DeepClone(),
            ["funding_need"] = summary?["funding_need"]?.DeepClone(),
            ["payback_month"] = summary?["payback_month"]?.DeepClone(),
            ["ltv_to_cac"] = r?["unit_economics"]?["ltv_to_cac"]?.DeepClone()
        };
    }

    private static string? Str(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static List<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
                .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList()
            : [];

    /// <summary>Parses a report argument, or null when it isn't a JSON object.</summary>
    public static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
