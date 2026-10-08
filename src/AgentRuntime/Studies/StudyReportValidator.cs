using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentRuntime.Studies;

/// <summary>
/// Checks a study report before the run can finish (docs/studies.md, "What the runtime enforces"):
/// every cited evidence id and model exists in this study, every model a finding relies on was
/// accepted by an agent other than its author, and every source has a role in the data-use plan.
/// Findings without evidence are kept but marked as interpretation, and findings resting on
/// simulated evidence are marked as simulated.
/// </summary>
public static class StudyReportValidator
{
    public sealed record Input(
        JsonObject Report,
        IReadOnlyDictionary<string, StudyEvidence> Evidence,
        IReadOnlyDictionary<string, StudyModel> Models,
        IReadOnlyCollection<string> Sources,
        IReadOnlyDictionary<string, SourceRoleEntry> Roles);

    public sealed record Result(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, JsonObject Report)
    {
        public bool Ok => Errors.Count == 0;
    }

    public static readonly string[] Confidences = ["high", "medium", "low"];

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
            finding["evidence_kinds"] = new JsonArray(known.Select(e => e.Kind).Distinct().Order().Select(k => (JsonNode)k!).ToArray());
            var confidence = Str(finding, "confidence")?.ToLowerInvariant();
            finding["confidence"] = Confidences.Contains(confidence) ? confidence : supported ? "medium" : "low";
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
