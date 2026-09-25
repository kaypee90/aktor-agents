using System.Text;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.LLM;

/// <summary>
/// Summarizes an agent's older history so it isn't resent in full on every LLM call. Uses the
/// fast model tier, keeps ids, numbers, decisions and open items, and falls back to a plain
/// excerpt if the model doesn't answer with text — compaction must never lose the agent's work
/// outright or block its turn.
/// </summary>
public sealed class ContextCompactor(ILLMProvider llm, IOptions<LlmOptions> options, ILogger<ContextCompactor> logger)
{
    public const string SystemMarker = "You compress an agent's working history";
    private const int MaxSummaryChars = 4000;

    public sealed record Result(string Summary, LlmCompletionResponse? Usage);

    public async Task<Result> SummarizeAsync(AgentState agent, IReadOnlyList<AgentTranscriptEntry> dropped, CancellationToken ct = default)
    {
        var history = new StringBuilder();
        foreach (var e in dropped)
        {
            var body = e.ToolCalls is { Count: > 0 }
                ? string.Join("; ", e.ToolCalls.Select(c => $"called {c.Name}({Clip(c.ArgumentsJson, 300)})")) + (e.Content is { Length: > 0 } ? " " + Clip(e.Content, 600) : string.Empty)
                : Clip(e.Content ?? string.Empty, e.Role == "tool" ? 800 : 1200);
            history.Append('[').Append(e.Role).Append(e.ToolName is null ? string.Empty : ":" + e.ToolName).Append("] ").AppendLine(body);
        }

        var prompt = $"""
            Agent: {agent.Name} ({agent.Role}). Goal: {agent.Goal}

            Previous summary:
            {agent.ContextSummary ?? "(none)"}

            Older history to fold in:
            {history}
            """;

        try
        {
            var opts = options.Value;
            var response = await llm.CompleteAsync(new LlmCompletionRequest
            {
                Messages =
                [
                    ChatMessage.System(SystemMarker + " into a compact briefing it will rely on later. Keep: " +
                                       "facts learned, decisions and why, results, ids/numbers/names, what was sent to whom, " +
                                       "commitments and open items. Drop chatter and anything superseded. Plain text, at most 250 words."),
                    ChatMessage.User(prompt)
                ],
                Model = opts.ModelFor(fast: true),
                MaxTokens = 600,
                Temperature = 0.2
            }, ct);

            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                return new Result(Clip(response.Content.Trim(), MaxSummaryChars), response);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Summarizing history for agent {AgentId} failed; keeping an excerpt instead", agent.AgentId);
        }

        // Fallback: previous summary plus the most recent part of what was dropped.
        var excerpt = (agent.ContextSummary is null ? string.Empty : agent.ContextSummary + "\n") + history;
        return new Result(excerpt.Length > MaxSummaryChars ? "…" + excerpt[^MaxSummaryChars..] : excerpt, null);
    }

    public static int EstimateTokens(IEnumerable<AgentTranscriptEntry> entries) =>
        entries.Sum(e => (e.Content?.Length ?? 0) + (e.ToolCalls?.Sum(c => c.ArgumentsJson.Length + c.Name.Length) ?? 0)) / 4;

    private static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}

/// <summary>The compacted history, near the end of the prompt (it changes when compaction runs).</summary>
public sealed class ContextSummarySection : ISystemPromptSection
{
    public string Header => "EARLIER CONTEXT (SUMMARY)";
    public bool IsDynamic => true;

    public string Render(AgentPromptContext context) => context.State.ContextSummary ?? string.Empty;
}
