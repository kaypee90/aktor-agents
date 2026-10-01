using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentRuntime.Durability;

public enum StepDiffStatus
{
    Same,
    Different,
    OnlyInA,
    OnlyInB
}

public sealed record StepDiff(
    string AgentPath,
    string Kind,
    string Key,
    int Step,
    string? ToolName,
    StepDiffStatus Status,
    long? SeqA,
    long? SeqB,
    string? SummaryA,
    string? SummaryB);

public sealed record RunDiffResult(
    int Same,
    int Different,
    int OnlyInA,
    int OnlyInB,
    IReadOnlyList<string> AgentsOnlyInA,
    IReadOnlyList<string> AgentsOnlyInB,
    IReadOnlyList<StepDiff> Steps)
{
    public bool Identical => Different == 0 && OnlyInA == 0 && OnlyInB == 0;
}

/// <summary>
/// Compares two runs step by step (roadmap P6), aligned by each agent's position in the tree, so a
/// replay or a fork can be checked against its original. Agent and message ids differ between runs
/// by design; they're normalized to tree paths before comparing, so only real differences show.
/// </summary>
public static partial class RunDiff
{
    public static RunDiffResult Compare(IReadOnlyList<JournalStep> a, IReadOnlyList<JournalStep> b)
    {
        // One map for both runs: a replay serves recorded results that name the original's agents,
        // and paths line up across runs, so every id from either run maps to its tree position.
        var ids = a.Concat(b).GroupBy(s => s.AgentId).ToDictionary(g => g.Key, g => g.First().AgentPath);
        var byKeyA = a.ToDictionary(Key);
        var byKeyB = b.ToDictionary(Key);

        var steps = new List<StepDiff>();
        foreach (var key in byKeyA.Keys.Union(byKeyB.Keys))
        {
            byKeyA.TryGetValue(key, out var sa);
            byKeyB.TryGetValue(key, out var sb);
            var any = sa ?? sb!;
            var status = sa is null ? StepDiffStatus.OnlyInB
                : sb is null ? StepDiffStatus.OnlyInA
                : Normalize(sa.PayloadJson, ids) == Normalize(sb.PayloadJson, ids) ? StepDiffStatus.Same
                : StepDiffStatus.Different;
            steps.Add(new StepDiff(any.AgentPath, any.Kind, any.Key, any.Step, any.ToolName, status, sa?.Seq, sb?.Seq,
                sa is null ? null : Summarize(sa), sb is null ? null : Summarize(sb)));
        }

        var pathsA = a.Select(s => s.AgentPath).ToHashSet();
        var pathsB = b.Select(s => s.AgentPath).ToHashSet();
        var ordered = steps.OrderBy(s => s.SeqA ?? long.MaxValue).ThenBy(s => s.SeqB ?? long.MaxValue).ToList();
        return new RunDiffResult(
            steps.Count(s => s.Status == StepDiffStatus.Same),
            steps.Count(s => s.Status == StepDiffStatus.Different),
            steps.Count(s => s.Status == StepDiffStatus.OnlyInA),
            steps.Count(s => s.Status == StepDiffStatus.OnlyInB),
            pathsA.Except(pathsB).Order().ToList(),
            pathsB.Except(pathsA).Order().ToList(),
            ordered);
    }

    private static (string, string, string) Key(JournalStep s) => (s.AgentPath, s.Kind, s.Key);

    /// <summary>The payload with run-specific ids replaced: agents by their tree path, messages and
    /// generated ids by placeholders. Usage counts (cost, not behavior) and wall-clock time budgets
    /// (what's left of the parent's time when a child is spawned) are dropped.</summary>
    public static string Normalize(string payloadJson, IReadOnlyDictionary<string, string> agentPaths)
    {
        string text;
        try
        {
            var node = JsonNode.Parse(payloadJson);
            // A tool's result is JSON inside a string: compare it as JSON too.
            if (node is JsonObject obj && obj["ResultJson"] is JsonValue inner && inner.TryGetValue<string>(out var innerText))
            {
                try { obj["ResultJson"] = JsonNode.Parse(innerText); } catch (JsonException) { /* plain text result */ }
            }

            Strip(node);
            text = node?.ToJsonString(Relaxed) ?? payloadJson;
        }
        catch (JsonException)
        {
            text = payloadJson;
        }

        foreach (var (id, path) in agentPaths.OrderByDescending(p => p.Key.Length))
        {
            text = text.Replace(id, "<" + path + ">", StringComparison.Ordinal);
        }

        return GeneratedId().Replace(text, m => m.Groups[1].Value + "*");
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).Where(k => UsageFields.Contains(k) || k == "max_duration_seconds").ToList()) o.Remove(key);
                foreach (var (_, child) in o) Strip(child);
                break;
            case JsonArray a:
                foreach (var child in a) Strip(child);
                break;
        }
    }

    private static readonly HashSet<string> UsageFields = ["InputTokens", "OutputTokens", "CachedInputTokens", "CacheWriteInputTokens"];

    /// <summary>One line for people: what the model decided, or what a tool returned.</summary>
    public static string Summarize(JournalStep step)
    {
        try
        {
            using var doc = JsonDocument.Parse(step.PayloadJson);
            var root = doc.RootElement;
            if (step.Kind == JournalStep.LlmKind)
            {
                var calls = root.TryGetProperty("ToolCalls", out var tc) ? tc.EnumerateArray().Select(c => c.GetProperty("Name").GetString()).ToList() : [];
                var content = root.TryGetProperty("Content", out var c2) && c2.ValueKind == JsonValueKind.String ? c2.GetString() : null;
                var said = string.IsNullOrWhiteSpace(content) ? string.Empty : $"\"{Clip(content!, 80)}\" ";
                return calls.Count == 0 ? $"{said}(no tool call)" : $"{said}→ {string.Join(", ", calls)}";
            }

            var ok = root.GetProperty("Success").GetBoolean();
            return ok
                ? "ok: " + Clip(root.TryGetProperty("ResultJson", out var r) ? r.GetString() ?? string.Empty : string.Empty, 100)
                : "failed: " + Clip(root.TryGetProperty("Error", out var e) ? e.GetString() ?? string.Empty : string.Empty, 100);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Clip(step.PayloadJson, 100);
        }
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    /// <summary>Ids generated per run: msg-…, agent-…, root-…, call_… from a live model.</summary>
    [GeneratedRegex(@"\b(msg-|agent-|root-)[0-9a-f]{6,}\b")]
    private static partial Regex GeneratedId();

}
