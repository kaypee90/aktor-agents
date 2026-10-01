using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.LLM;

namespace AgentRuntime.Resources;

/// <summary>One agent the root would likely start, as the planning call sees it.</summary>
public sealed record PlannedRole(string Role, string Purpose, int Depth, string? ParentRole);

/// <summary>The team a goal is likely to need: the root (depth 0) and the agents below it.</summary>
public sealed record TeamPlan
{
    public required string Goal { get; init; }
    public string? GoalType { get; init; }
    public List<PlannedRole> Team { get; init; } = [];
    public string? Rationale { get; init; }
    public int TeamSize => Team.Count;
    public int MaxDepth => Team.Count == 0 ? 0 : Team.Max(t => t.Depth);
}

/// <summary>Ranges, not a promise: the budget, not the estimate, is what the runtime enforces.</summary>
public sealed record TaskEstimate
{
    public long TokensLow { get; init; }
    public long TokensExpected { get; init; }
    public long TokensHigh { get; init; }
    public decimal CostUsdLow { get; init; }
    public decimal CostUsdExpected { get; init; }
    public decimal CostUsdHigh { get; init; }
    public int DurationSecondsLow { get; init; }
    public int DurationSecondsExpected { get; init; }
    public int DurationSecondsHigh { get; init; }
    public int TeamSize { get; init; }
}

/// <summary>What past tasks of the same organization say about how estimates turn out.</summary>
public sealed record EstimatorCalibration
{
    /// <summary>Completed tasks the numbers come from; 0 means the configured defaults are used.</summary>
    public int Samples { get; init; }
    public double TokensPerAgent { get; init; }
    /// <summary>Agents a team actually ends up with, per agent the preview planned.</summary>
    public double TeamSizeFactor { get; init; } = 1;
    public double SecondsPerLevel { get; init; }
    public string Source => Samples > 0 ? "history" : "defaults";
}

/// <summary>Section "Preview".</summary>
public sealed class PreviewOptions
{
    public const string SectionName = "Preview";

    /// <summary>Above this expected-high cost the dashboard asks the user to confirm before starting.</summary>
    public decimal ConfirmAboveUsd { get; set; } = 2.00m;

    /// <summary>Tokens per agent before there's any history to calibrate on (a few steps of a real model).</summary>
    public int DefaultTokensPerAgent { get; set; } = 40_000;
    public int DefaultSecondsPerLevel { get; set; } = 90;

    /// <summary>Share of tokens that are output (priced higher).</summary>
    public double OutputTokenShare { get; set; } = 0.08;

    /// <summary>Low and high ends of each range, relative to the expected value.</summary>
    public double LowFactor { get; set; } = 0.5;
    public double HighFactor { get; set; } = 2.0;

    /// <summary>Recent completed tasks used to calibrate estimates.</summary>
    public int CalibrationTasks { get; set; } = 20;

    /// <summary>A planned team is cut to this many agents (a plan bigger than the limits allows can't happen).</summary>
    public int MaxPlannedAgents { get; set; } = 25;

    public int PlanningMaxOutputTokens { get; set; } = 1200;
}

/// <summary>
/// The planning call behind a preview (roadmap P2): one LLM call, with a single structured tool,
/// that sketches the team the root agent would build. It runs no agent and costs a fraction of a
/// task; the runtime's limits are given to the model so it doesn't plan a team that can't exist.
/// </summary>
public static class TeamPreviewPrompt
{
    public const string ToolName = "propose_team";

    public static readonly LlmToolDefinition Tool = new()
    {
        Name = ToolName,
        Description = "Describe the agent team you would build for this goal.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "goal_type": { "type": "string", "description": "One or two words: research, coding, analysis, writing, operations…" },
            "team": {
              "type": "array",
              "description": "Agents you would start, not counting yourself (the root). Empty if you'd do it alone.",
              "items": {
                "type": "object",
                "properties": {
                  "role": { "type": "string" },
                  "purpose": { "type": "string" },
                  "parent_role": { "type": "string", "description": "The role that would start this agent; omit for agents you start yourself." }
                },
                "required": ["role", "purpose"]
              }
            },
            "rationale": { "type": "string", "description": "One sentence on why this shape." }
          },
          "required": ["team"]
        }
        """
    };

    public static LlmCompletionRequest Build(string goal, ResourceBudget budget, RuntimeLimitsOptions limits, string? model, int maxOutputTokens) => new()
    {
        Model = model,
        MaxTokens = maxOutputTokens,
        Temperature = 0.2,
        Tools = [Tool],
        Messages =
        [
            ChatMessage.System(
                "## ROLE\nYou plan agent teams for an autonomous multi-agent runtime, before any agent runs.\n\n" +
                "## TASK\nSketch the team a root agent would build for the goal below, and call propose_team. Be realistic: " +
                "a specialist costs a full prompt per step, so add one only for a substantial part of the goal that runs in " +
                "parallel or needs its own expertise. Small goals need no team.\n\n" +
                $"## LIMITS\nThe root may start at most {budget.MaxChildren} agents; any agent at most {limits.MaxChildrenPerAgent}; " +
                $"tree depth at most {limits.MaxAgentDepth}. The whole team shares {budget.MaxTokens:N0} tokens and ${budget.MaxCostUsd:F2}."),
            ChatMessage.User($"Goal: {goal}")
        ]
    };

    /// <summary>Reads the planning call's answer. A model that doesn't call the tool, or calls it
    /// badly, gets a root-only plan rather than an error: the preview must never block work.</summary>
    public static TeamPlan Parse(string goal, LlmCompletionResponse response, int maxAgents)
    {
        var call = response.ToolCalls.FirstOrDefault(c => c.Name == ToolName);
        var root = new PlannedRole("Root Agent", "Plans the work, delegates, and writes the final result.", 0, null);
        if (call is null) return new TeamPlan { Goal = goal, Team = [root], Rationale = response.Content };

        Args? args;
        try
        {
            args = JsonSerializer.Deserialize<Args>(call.ArgumentsJson, Json);
        }
        catch (JsonException)
        {
            return new TeamPlan { Goal = goal, Team = [root] };
        }

        var team = new List<PlannedRole> { root };
        var depthByRole = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Root Agent"] = 0 };
        // Parents first: agents whose parent is planned later are placed after it.
        var pending = (args?.Team ?? []).Where(m => !string.IsNullOrWhiteSpace(m.Role)).ToList();
        for (var pass = 0; pass < 6 && pending.Count > 0; pass++)
        {
            foreach (var m in pending.ToList())
            {
                var parent = string.IsNullOrWhiteSpace(m.ParentRole) ? "Root Agent" : m.ParentRole!;
                if (!depthByRole.TryGetValue(parent, out var parentDepth))
                {
                    if (pass < 5) continue;
                    parent = "Root Agent"; // an unknown parent: assume the root starts it
                    parentDepth = 0;
                }

                var depth = parentDepth + 1;
                team.Add(new PlannedRole(m.Role!.Trim(), m.Purpose?.Trim() ?? string.Empty, depth, parent));
                depthByRole.TryAdd(m.Role.Trim(), depth);
                pending.Remove(m);
            }
        }

        return new TeamPlan
        {
            Goal = goal,
            GoalType = string.IsNullOrWhiteSpace(args?.GoalType) ? null : args!.GoalType!.Trim().ToLowerInvariant(),
            Team = team.Take(Math.Max(1, maxAgents)).ToList(),
            Rationale = args?.Rationale
        };
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    private sealed record Args(string? GoalType, List<Member>? Team, string? Rationale);

    private sealed record Member(string? Role, string? Purpose, [property: JsonPropertyName("parent_role")] string? ParentRole);
}

/// <summary>
/// Turns a planned team into token, dollar and time ranges. Pure: the planned team size times the
/// tokens an agent of this organization has typically used (calibrated from its recent tasks), at the
/// model's prices, with every high end capped by the budget, since the runtime stops the team there.
/// </summary>
public static class CostEstimator
{
    public static TaskEstimate Estimate(TeamPlan plan, EstimatorCalibration calibration, ResourceBudget budget, LlmOptions prices, PreviewOptions options)
    {
        var tokensPerAgent = calibration.TokensPerAgent > 0 ? calibration.TokensPerAgent : options.DefaultTokensPerAgent;
        var agents = Math.Max(1, plan.TeamSize * Math.Clamp(calibration.TeamSizeFactor, 0.25, 4));
        var expectedTokens = (long)Math.Round(agents * tokensPerAgent);

        var outShare = (decimal)Math.Clamp(options.OutputTokenShare, 0, 1);
        var perToken = (1 - outShare) * prices.PricePerInputTokenUsd + outShare * prices.PricePerOutputTokenUsd;

        long Cap(long tokens) => Math.Min(tokens, budget.MaxTokens);
        decimal Cost(long tokens) => Math.Min(Math.Round(tokens * perToken, 4), budget.MaxCostUsd);

        var secondsPerLevel = calibration.SecondsPerLevel > 0 ? calibration.SecondsPerLevel : options.DefaultSecondsPerLevel;
        var expectedSeconds = (plan.MaxDepth + 1) * secondsPerLevel;
        int Seconds(double s) => (int)Math.Min(Math.Round(s), budget.MaxDurationSeconds);

        var low = Cap((long)(expectedTokens * options.LowFactor));
        var expected = Cap(expectedTokens);
        var high = Cap((long)(expectedTokens * options.HighFactor));
        return new TaskEstimate
        {
            TokensLow = low,
            TokensExpected = expected,
            TokensHigh = high,
            CostUsdLow = Cost(low),
            CostUsdExpected = Cost(expected),
            CostUsdHigh = Cost(high),
            DurationSecondsLow = Seconds(expectedSeconds * options.LowFactor),
            DurationSecondsExpected = Seconds(expectedSeconds),
            DurationSecondsHigh = Seconds(expectedSeconds * options.HighFactor),
            TeamSize = (int)Math.Round(agents)
        };
    }

    /// <summary>True when the budget, not the plan, sets the high end: the team will be stopped (and
    /// report what it has) before spending what the plan suggests it could.</summary>
    public static bool CappedByBudget(TeamPlan plan, EstimatorCalibration calibration, ResourceBudget budget, LlmOptions prices, PreviewOptions options)
    {
        var uncapped = Estimate(plan, calibration, budget with { MaxTokens = int.MaxValue, MaxCostUsd = decimal.MaxValue / 1_000_000 }, prices, options);
        return uncapped.TokensHigh > budget.MaxTokens || uncapped.CostUsdHigh > budget.MaxCostUsd;
    }

    /// <summary>Calibration from past tasks: each sample is one finished task's agents, tokens,
    /// duration, depth and (if it had a preview) the team size the preview planned.</summary>
    public static EstimatorCalibration Calibrate(IReadOnlyList<CalibrationSample> samples)
    {
        var usable = samples.Where(s => s.Agents > 0 && s.Tokens > 0).ToList();
        if (usable.Count == 0) return new EstimatorCalibration();

        var planned = usable.Where(s => s.PlannedTeamSize > 0).ToList();
        return new EstimatorCalibration
        {
            Samples = usable.Count,
            TokensPerAgent = Median(usable.Select(s => (double)s.Tokens / s.Agents)),
            TeamSizeFactor = planned.Count == 0 ? 1 : Median(planned.Select(s => (double)s.Agents / s.PlannedTeamSize)),
            SecondsPerLevel = Median(usable.Where(s => s.DurationSeconds > 0).Select(s => s.DurationSeconds / (s.MaxDepth + 1.0)).DefaultIfEmpty(0))
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

public sealed record CalibrationSample(int Agents, long Tokens, double DurationSeconds, int MaxDepth, int PlannedTeamSize);
