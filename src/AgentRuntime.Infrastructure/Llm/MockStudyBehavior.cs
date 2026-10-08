using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.LLM;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// How the mock "brain" works on a study (docs/studies.md), so a study runs end to end with
/// Llm:Provider=Mock: the lead reads the sources, gives each a role, records a hypothesis, fits a
/// model on the first dataset, has a reviewer agent accept it, scores it on the holdout, runs a
/// small simulation and submits a report citing the evidence. Simulated participants decide by a
/// hash of their prompt. Real reasoning comes from a real model; this only exercises the flow.
/// </summary>
internal static partial class MockStudyBehavior
{
    public static bool IsParticipant(LlmCompletionRequest request) => request.Tools.Count == 1 && request.Tools[0].Name == "decide";

    public static bool IsStudyAgent(LlmCompletionRequest request) => request.Tools.Any(t => t.Name == "study_sources");

    public static LlmCompletionResponse Participant(LlmCompletionRequest request)
    {
        var prompt = string.Concat(request.Messages.Select(m => m.Content));
        var schema = JsonNode.Parse(request.Tools[0].JsonSchema);
        var options = (schema?["properties"]?["choice"]?["enum"] as JsonArray)?.Select(n => n!.ToString()).ToList() ?? ["yes", "no"];
        // Mostly the first option, more often a later one when the prompt talks about increases.
        var hash = (uint)prompt.GetHashCode(StringComparison.Ordinal);
        var pressure = Regex.IsMatch(prompt, @"\b(increase|rise|raise|higher)\b", RegexOptions.IgnoreCase) ? 25 : 0;
        var roll = (int)(hash % 100);
        var index = roll < 65 - pressure ? 0 : 1 + (int)(hash / 100 % (uint)Math.Max(1, options.Count - 1));
        var args = new JsonObject { ["choice"] = options[Math.Min(index, options.Count - 1)], ["reason"] = "Mock participant: decided by a hash of the prompt." };
        if (schema?["properties"]?["value"] is not null)
        {
            var range = Regex.Match(schema["properties"]!["value"]!["description"]?.ToString() ?? string.Empty, @"between (-?[\d.]+) and (-?[\d.]+)");
            args["value"] = range.Success ? (double.Parse(range.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                                            + double.Parse(range.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) / 2 : 0;
        }

        return Calls(prompt, new ToolCall { Id = NewId(), Name = "decide", ArgumentsJson = args.ToJsonString() });
    }

    public static LlmCompletionResponse Agent(LlmCompletionRequest request, string systemText, bool isRoot, string goal)
    {
        var history = new History(request);
        if (!isRoot) return Reviewer(history, goal, systemText);

        // 1. What the study has.
        if (!history.Called("study_sources")) return Calls(systemText, Call("study_sources", new { }));
        var sources = history.LastResult("study_sources") ?? new JsonObject();

        // 2. A role for every source.
        var unassigned = (sources["sources_without_a_role"] as JsonArray)?.Select(n => n!.ToString()).ToList() ?? [];
        if (unassigned.Count > 0 && history.Count("set_source_role") < unassigned.Count + 2)
        {
            return Calls(systemText, unassigned.Select(source => Call("set_source_role", source.StartsWith("connection:", StringComparison.Ordinal)
                ? new { source, role = "not_relevant", reason = "The mock analysis doesn't call connections." }
                : (object)new { source, role = source.StartsWith("dataset:", StringComparison.Ordinal) ? "model_input" : "scenario" })).ToArray());
        }

        var plan = ModelPlan(sources);
        if (plan is null) return Report(history, systemText, null);

        // 3. A hypothesis, then a summary query and the model that tests it.
        if (!history.Called("record_hypothesis"))
        {
            return Calls(systemText, Call("record_hypothesis", new { statement = $"{string.Join(", ", plan.Value.Features)} explain {plan.Value.Target}.", rationale = "Mock plan." }));
        }

        if (!history.Called("fit_model"))
        {
            var hypothesis = history.LastResult("record_hypothesis")?["hypothesis_id"]?.ToString();
            return Calls(systemText,
                Call("query_dataset", new { sql = $"SELECT count(*) AS n, avg({plan.Value.Target}) AS mean_target FROM {plan.Value.Dataset}" }),
                Call("fit_model", new { method = plan.Value.Method, dataset = plan.Value.Dataset, target = plan.Value.Target, features = plan.Value.Features, hypothesis_id = hypothesis }));
        }

        var model = history.LastResult("fit_model");
        var modelId = model?["model_id"]?.ToString();
        if (modelId is null) return Report(history, systemText, null);

        // 4. Another agent reviews it; wait for it.
        if (!history.Called("spawn_agent"))
        {
            return Calls(systemText, Call("spawn_agent", new
            {
                role = "Model Reviewer",
                goal = $"Review model {modelId} ({plan.Value.Method} of {plan.Value.Target}) for leakage, overfitting and assumptions.",
                capabilities = new[] { "research" },
                why_not_myself = "A model can't be reviewed by the agent that fitted it."
            }));
        }

        if (!history.ReviewerReported) return new LlmCompletionResponse { Content = "Waiting for the reviewer.", FinishReason = LlmFinishReason.Stop, InputTokens = Tokens(systemText), OutputTokens = 10 };

        // 5. Score it on the sealed holdout, run a small experiment, then report.
        if (!history.Called("evaluate_on_holdout")) return Calls(systemText, Call("evaluate_on_holdout", new { model_id = modelId }));
        if (!history.Called("run_simulation") && history.LastResult("query_dataset")?["evidence_id"]?.ToString() is { } queryEvidence)
        {
            return Calls(systemText, Call("run_simulation", new
            {
                name = "mock-scenario",
                population = new
                {
                    size = 6,
                    evidence = new[] { queryEvidence },
                    segments = new object[]
                    {
                        new { name = "long-standing", share = 0.5, description = "You have been a customer for years.", attributes = new { tenure = "long" } },
                        new { name = "new", share = 0.5, description = "You joined recently.", attributes = new { tenure = "short" } }
                    }
                },
                conditions = new object[]
                {
                    new { name = "control", scenario = "Nothing changes this year." },
                    new { name = "increase", scenario = "Prices increase by 8% this year." }
                },
                decision = new { question = "Do you stay?", options = new[] { "stay", "leave" } }
            }));
        }

        return Report(history, systemText, modelId);
    }

    private static LlmCompletionResponse Report(History history, string systemText, string? modelId)
    {
        if (!history.Called("submit_report") || history.LastResultFailed("submit_report") && history.Count("submit_report") < 2)
        {
            var evidence = history.EvidenceIds().ToList();
            var findings = new List<object>();
            if (modelId is not null && history.LastResult("study_sources")?["models"] is not null)
            {
                findings.Add(new { claim = "The fitted model describes the target in the training data (see its coefficients and holdout score).", evidence, models = new[] { modelId }, confidence = "medium" });
            }

            findings.Add(new { claim = "The study's sources were reviewed and each given a role.", evidence = evidence.Take(1).ToArray(), confidence = "low" });
            return Calls(systemText, Call("submit_report", new
            {
                summary = "Mock study report: the analysis ran end to end on the study's data. A real model writes the actual findings.",
                findings,
                limitations = new[] { "Produced by the mock model: the statistics are real, the interpretation is not." },
                open_questions = new[] { "What would a real model conclude from the same evidence?" }
            }));
        }

        if (!history.Called("complete_task") || history.LastResultFailed("complete_task") && history.Count("complete_task") < 3)
        {
            var status = history.LastResultFailed("submit_report") ? "partial" : "completed";
            return Calls(systemText, Call("complete_task", new { status, summary = "Mock study run finished; see the study report." }));
        }

        return new LlmCompletionResponse { Content = "Done.", FinishReason = LlmFinishReason.Stop, InputTokens = Tokens(systemText), OutputTokens = 5 };
    }

    private static LlmCompletionResponse Reviewer(History history, string goal, string systemText)
    {
        if (ModelIdPattern().Match(goal) is { Success: true } m && !history.Called("review_model"))
        {
            return Calls(systemText, Call("review_model", new { model_id = m.Value, verdict = "accept", notes = "Mock review: checked the diagnostics and warnings; nothing disqualifying." }));
        }

        return Calls(systemText, Call("complete_task", new { status = "completed", summary = $"Reviewed {(ModelIdPattern().Match(goal) is { Success: true } mm ? mm.Value : "the model")}: accepted." }));
    }

    /// <summary>The first dataset's model: logistic for a two-valued numeric column, else linear on
    /// its last numeric column, explained by up to three other numeric or categorical columns.</summary>
    private static (string Dataset, string Method, string Target, string[] Features)? ModelPlan(JsonObject sources)
    {
        foreach (var dataset in sources["datasets"] as JsonArray ?? [])
        {
            var columns = (dataset?["columns"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var numeric = columns.Where(c => c["kind"]?.ToString() == "numeric").ToList();
            var binary = numeric.FirstOrDefault(c => c["distinct"]?.GetValue<int>() == 2);
            var target = binary ?? numeric.LastOrDefault();
            if (target is null) continue;
            var features = columns.Where(c => c != target && c["kind"]?.ToString() is "numeric" or "categorical" or "boolean")
                .Select(c => c["name"]!.ToString()).Take(3).ToArray();
            if (features.Length == 0) continue;
            return (dataset!["table"]!.ToString(), binary is null ? "linear_regression" : "logistic_regression", target["name"]!.ToString(), features);
        }

        return null;
    }

    private sealed class History(LlmCompletionRequest request)
    {
        private readonly List<ToolCall> _calls = request.Messages.Where(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!).ToList();

        public bool Called(string name) => _calls.Any(c => c.Name == name);
        public int Count(string name) => _calls.Count(c => c.Name == name);

        private string? ResultText(string name)
        {
            var call = _calls.LastOrDefault(c => c.Name == name);
            return call is null ? null : request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolCallId == call.Id)?.Content;
        }

        public JsonObject? LastResult(string name)
        {
            try { return ResultText(name) is { } text ? JsonNode.Parse(text) as JsonObject : null; }
            catch (JsonException) { return null; }
        }

        public bool LastResultFailed(string name) => ResultText(name) is { } text && (text.Contains("\"error\"", StringComparison.Ordinal) || text.StartsWith("Error", StringComparison.Ordinal));

        /// <summary>Every evidence id a tool returned in this conversation.</summary>
        public IEnumerable<string> EvidenceIds() => request.Messages.Where(m => m.Role == ChatRole.Tool && m.Content is not null)
            .SelectMany(m => EvidencePattern().Matches(m.Content!).Select(x => x.Groups[1].Value)).Distinct();

        public bool ReviewerReported => request.Messages.Any(m => m.Role is ChatRole.User && m.Content is { } c
            && c.Contains("Model Reviewer", StringComparison.OrdinalIgnoreCase) && c.Contains("completed", StringComparison.OrdinalIgnoreCase));
    }

    private static ToolCall Call(string name, object args) => new() { Id = NewId(), Name = name, ArgumentsJson = JsonSerializer.Serialize(args) };

    private static LlmCompletionResponse Calls(string context, params ToolCall[] calls) => new()
    {
        ToolCalls = calls,
        FinishReason = LlmFinishReason.ToolCalls,
        InputTokens = Tokens(context),
        OutputTokens = 60
    };

    private static int Tokens(string text) => Math.Max(1, text.Length / 4);
    private static string NewId() => "call_" + Guid.NewGuid().ToString("n")[..12];

    [GeneratedRegex(@"mdl-[0-9a-f]{8}")]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex("\"evidence_id\"\\s*:\\s*\"(ev-[0-9a-f]{8})\"")]
    private static partial Regex EvidencePattern();
}
