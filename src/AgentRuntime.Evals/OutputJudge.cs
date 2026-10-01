using System.Text.Json;
using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Evals;

public sealed record Verdict(double Score, string Rationale);

/// <summary>Scores a run's output from 0 to 5.</summary>
public abstract class OutputJudge
{
    public abstract string Name { get; }

    public abstract Task<Verdict> ScoreAsync(EvalScenario scenario, string? summary, IReadOnlyList<string> findings,
        IReadOnlyList<string> artifacts, CancellationToken ct);

    /// <summary>"auto" uses the LLM judge with a real model and the heuristic one with the Mock provider
    /// (whose prose would make an LLM grade meaningless).</summary>
    public static OutputJudge Create(string kind, IServiceProvider services)
    {
        var provider = services.GetRequiredService<IOptions<LlmOptions>>().Value.Provider;
        var useLlm = kind == "llm" || (kind == "auto" && !provider.Equals("Mock", StringComparison.OrdinalIgnoreCase));
        return useLlm
            ? new LlmJudge(services.GetRequiredService<ILLMProvider>(), services.GetRequiredService<IOptions<LlmOptions>>().Value)
            : new HeuristicJudge();
    }
}

/// <summary>
/// Deterministic checks, one point each: there is a summary; it is about the goal (shares its
/// words); specialists contributed findings; a deliverable was written; and the expected topics are
/// covered (scaled by how many).
/// </summary>
public sealed class HeuristicJudge : OutputJudge
{
    public override string Name => "heuristic";

    public override Task<Verdict> ScoreAsync(EvalScenario scenario, string? summary, IReadOnlyList<string> findings,
        IReadOnlyList<string> artifacts, CancellationToken ct)
    {
        var notes = new List<string>();
        double score = 0;
        var output = string.Join("\n", new[] { summary ?? string.Empty }.Concat(findings));

        if (!string.IsNullOrWhiteSpace(summary)) { score++; notes.Add("summary"); }
        var goalWords = Words(scenario.Goal);
        if (Words(output).Intersect(goalWords).Count() >= Math.Min(2, goalWords.Count)) { score++; notes.Add("on topic"); }
        if (findings.Any(f => !string.IsNullOrWhiteSpace(f))) { score++; notes.Add($"{findings.Count} finding(s)"); }
        if (artifacts.Count > 0) { score++; notes.Add($"{artifacts.Count} artifact(s)"); }
        if (scenario.ExpectedTopics.Count > 0)
        {
            var covered = scenario.ExpectedTopics.Count(t => output.Contains(t, StringComparison.OrdinalIgnoreCase));
            score += (double)covered / scenario.ExpectedTopics.Count;
            notes.Add($"{covered}/{scenario.ExpectedTopics.Count} topics");
        }
        else
        {
            score++;
        }

        return Task.FromResult(new Verdict(Math.Round(score, 2), string.Join(", ", notes)));
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split([' ', ',', '.', ':', ';', '-', '\n', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4).ToHashSet();
}

/// <summary>An LLM grades the output against a rubric through a structured tool, so the score is a
/// number, not prose to parse.</summary>
public sealed class LlmJudge(ILLMProvider llm, LlmOptions options) : OutputJudge
{
    public override string Name => $"llm:{options.Model}";

    private static readonly LlmToolDefinition GradeTool = new()
    {
        Name = "grade_output",
        Description = "Grade the team's output.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "relevance": { "type": "integer", "minimum": 0, "maximum": 5, "description": "Answers the goal that was asked." },
            "completeness": { "type": "integer", "minimum": 0, "maximum": 5, "description": "Covers what the goal needs; nothing important missing." },
            "specificity": { "type": "integer", "minimum": 0, "maximum": 5, "description": "Concrete facts, numbers and names rather than generalities." },
            "actionability": { "type": "integer", "minimum": 0, "maximum": 5, "description": "Someone could act on it." },
            "rationale": { "type": "string" }
          },
          "required": ["relevance", "completeness", "specificity", "actionability", "rationale"]
        }
        """
    };

    public override async Task<Verdict> ScoreAsync(EvalScenario scenario, string? summary, IReadOnlyList<string> findings,
        IReadOnlyList<string> artifacts, CancellationToken ct)
    {
        var output = $"Summary:\n{summary}\n\nFindings:\n{string.Join("\n", findings.Select(f => "- " + f))}\n\nArtifacts: {string.Join(", ", artifacts)}";
        var topics = scenario.ExpectedTopics.Count == 0 ? string.Empty : $"\nA good answer covers: {string.Join(", ", scenario.ExpectedTopics)}.";
        var response = await llm.CompleteAsync(new LlmCompletionRequest
        {
            Messages =
            [
                ChatMessage.System("You grade the output of an autonomous agent team strictly and consistently. Call grade_output once."),
                ChatMessage.User($"Goal: {scenario.Goal}{topics}\n\n{output}")
            ],
            Tools = [GradeTool],
            Temperature = 0,
            MaxTokens = 600
        }, ct);

        var call = response.ToolCalls.FirstOrDefault(c => c.Name == GradeTool.Name);
        if (call is null) return new Verdict(0, "The judge didn't grade.");
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var r = doc.RootElement;
        double Get(string n) => r.TryGetProperty(n, out var v) && v.TryGetDouble(out var d) ? Math.Clamp(d, 0, 5) : 0;
        var score = (Get("relevance") + Get("completeness") + Get("specificity") + Get("actionability")) / 4;
        return new Verdict(Math.Round(score, 2), r.TryGetProperty("rationale", out var why) ? why.GetString() ?? string.Empty : string.Empty);
    }
}
