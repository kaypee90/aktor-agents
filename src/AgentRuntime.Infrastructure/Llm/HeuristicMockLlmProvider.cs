using System.Text.Json;
using AgentRuntime.LLM;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// Zero-configuration "brain" so the whole runtime can be demonstrated with Llm:Provider=Mock and
/// no API key. It inspects the transcript heuristically (not scripted per-scenario) to decide
/// whether to spawn, recurse once, or complete — enough to exercise spawning, recursive spawning,
/// tool use, and completion end-to-end. Real reasoning quality comes from <see cref="AnthropicProvider"/>
/// or <see cref="OpenAIProvider"/>; this exists purely so `docker compose up` works out of the box.
/// Deterministic *test* scenarios should use a purpose-scripted provider instead (CLAUDE.md section 43).
/// </summary>
public sealed class HeuristicMockLlmProvider : ILLMProvider
{
    public string ProviderName => "Mock";

    public Task<LlmCompletionResponse> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        // History compaction: answer with a (mock) summary, as a real model would.
        if (request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content?.StartsWith(AgentRuntime.LLM.ContextCompactor.SystemMarker) == true)
        {
            var text = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
            return Task.FromResult(new LlmCompletionResponse
            {
                Content = "Summary (mock): " + (text.Length > 400 ? text[..400] + "…" : text),
                FinishReason = LlmFinishReason.Stop,
                InputTokens = text.Length / 4,
                OutputTokens = 100
            });
        }

        // Simulation traffic is recognisable by its tools: genesis offers only define_world, and
        // every resident has end_turn.
        if (request.Tools.Any(t => t.Name == "define_world"))
        {
            return Task.FromResult(MockWorldBehavior.Genesis(request));
        }

        if (request.Tools.Any(t => t.Name == "end_turn"))
        {
            return Task.FromResult(MockWorldBehavior.Resident(request));
        }

        // Workspace agents: coordinators and standing agents have wait_for_events; one-shot
        // workers in a workspace have the workspace tools (e.g. notify_user) too.
        if (request.Tools.Any(t => t.Name is "wait_for_events" or "notify_user"))
        {
            return Task.FromResult(MockWorkspaceBehavior.Respond(request));
        }

        var systemText = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
        var role = ExtractBetween(systemText, "acting as: ", ".") ?? "Agent";
        var goal = ExtractSection(systemText, "## GOAL") ?? "the assigned goal";
        var isRoot = role.Contains("Root", StringComparison.OrdinalIgnoreCase);

        var priorToolCalls = request.Messages
            .Where(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!)
            .ToList();

        bool HasCalled(string name) => priorToolCalls.Any(c => c.Name == name);
        var availableTools = request.Tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var calls = new List<ToolCall>();

        if (isRoot)
        {
            if (!HasCalled("spawn_agent") && availableTools.Contains("spawn_agent"))
            {
                calls.Add(SpawnCall("Research Agent", $"Research the market and competitors for: {goal}", ["research", "web-search"]));
                calls.Add(SpawnCall("Technical Architecture Agent", $"Propose a technical architecture for: {goal}", ["postgresql", "database-design"]));
            }
            else if (!HasCalled("find_agents") && availableTools.Contains("find_agents"))
            {
                calls.Add(new ToolCall { Id = NewId(), Name = "find_agents", ArgumentsJson = "{}" });
            }
            else if (!HasCalled("filesystem_write") && availableTools.Contains("filesystem_write"))
            {
                // Produces an actual inspectable final artifact (CLAUDE.md section 26/52) rather
                // than leaving the "final result" as just a one-line summary.
                var team = ExtractFindAgentsRoles(request);
                var report = BuildReport(goal, team);
                calls.Add(new ToolCall
                {
                    Id = NewId(),
                    Name = "filesystem_write",
                    ArgumentsJson = JsonSerializer.Serialize(new { path = "final-report.md", content = report })
                });
            }
            else
            {
                calls.Add(CompleteCall($"Delegated '{goal}' to {ExtractFindAgentsRoles(request).Count} specialized sub-agents; see final-report.md for the consolidated write-up.",
                    artifacts: ["final-report.md"]));
            }
        }
        else
        {
            var isResearchLike = role.Contains("Research", StringComparison.OrdinalIgnoreCase) ||
                                  role.Contains("Architecture", StringComparison.OrdinalIgnoreCase);

            if (isResearchLike && availableTools.Contains("spawn_agent") && !HasCalled("spawn_agent"))
            {
                calls.Add(SpawnCall("Detail Agent", $"Gather supporting detail for: {goal}", ["research"]));
            }
            else
            {
                calls.Add(CompleteCall($"Completed: {goal}"));
            }
        }

        return Task.FromResult(new LlmCompletionResponse
        {
            Content = null,
            ToolCalls = calls,
            FinishReason = LlmFinishReason.ToolCalls,
            InputTokens = EstimateTokens(systemText),
            OutputTokens = 60
        });
    }

    private static ToolCall SpawnCall(string subRole, string subGoal, string[] capabilities) => new()
    {
        Id = NewId(),
        Name = "spawn_agent",
        ArgumentsJson = JsonSerializer.Serialize(new { role = subRole, goal = subGoal, capabilities })
    };

    private static ToolCall CompleteCall(string summary, string[]? artifacts = null) => new()
    {
        Id = NewId(),
        Name = "complete_task",
        ArgumentsJson = JsonSerializer.Serialize(new { status = "completed", summary, artifacts = artifacts ?? [] })
    };

    private static List<(string Role, string Goal)> ExtractFindAgentsRoles(LlmCompletionRequest request)
    {
        var team = new List<(string, string)>();
        var resultMessage = request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolName == "find_agents");
        if (resultMessage?.Content is null) return team;

        try
        {
            using var doc = JsonDocument.Parse(resultMessage.Content);
            foreach (var entry in doc.RootElement.GetProperty("agents").EnumerateArray())
            {
                var role = entry.GetProperty("role").GetString() ?? "Agent";
                if (role.Equals("Root Agent", StringComparison.OrdinalIgnoreCase)) continue;
                team.Add((role, entry.GetProperty("goal").GetString() ?? string.Empty));
            }
        }
        catch (JsonException)
        {
            // Best-effort — an empty team list just means a shorter report.
        }

        return team;
    }

    private static string BuildReport(string goal, List<(string Role, string Goal)> team)
    {
        var lines = new List<string>
        {
            $"# Final Report: {goal}",
            "",
            $"_Generated {DateTimeOffset.UtcNow:u} by the Root Agent, consolidating {team.Count} sub-agent(s)._",
            "",
            "## Summary",
            $"The root agent delegated \"{goal}\" across {team.Count} specialized sub-agents and",
            "reviewed their status via find_agents before consolidating this report.",
            "",
            "## Participating agents"
        };

        foreach (var (role, subGoal) in team)
        {
            lines.Add($"- **{role}**: {subGoal}");
        }

        lines.Add("");
        lines.Add("## Notes");
        lines.Add("This report was produced by the zero-config Mock LLM provider for demonstration");
        lines.Add("purposes. Configure a real provider (Anthropic/OpenAI) for substantive findings.");

        return string.Join('\n', lines);
    }

    private static string NewId() => "call_" + Guid.NewGuid().ToString("n")[..12];

    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    private static string? ExtractBetween(string text, string start, string end)
    {
        var i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = text.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? null : text[i..j];
    }

    private static string? ExtractSection(string text, string header)
    {
        var i = text.IndexOf(header, StringComparison.Ordinal);
        if (i < 0) return null;
        i += header.Length;
        var j = text.IndexOf("\n##", i, StringComparison.Ordinal);
        var section = j < 0 ? text[i..] : text[i..j];
        return section.Trim();
    }
}
