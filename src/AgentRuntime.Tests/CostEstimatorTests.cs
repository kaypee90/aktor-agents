using System.Text.Json;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.LLM;
using AgentRuntime.Resources;

namespace AgentRuntime.Tests;

/// <summary>Roadmap P2: turning a planned team into cost ranges, calibrating on history, and reading the planning call.</summary>
public sealed class CostEstimatorTests
{
    private static readonly LlmOptions Prices = new() { PricePerInputTokenUsd = 0.000003m, PricePerOutputTokenUsd = 0.000015m };
    private static readonly PreviewOptions Options = new();

    private static TeamPlan Plan(int agents, int depth = 1) => new()
    {
        Goal = "g",
        Team = Enumerable.Range(0, agents).Select(i => new PlannedRole($"R{i}", "p", i == 0 ? 0 : Math.Min(depth, i), i == 0 ? null : "Root Agent")).ToList()
    };

    [Fact]
    public void Ranges_scale_with_team_size_and_price()
    {
        var e = CostEstimator.Estimate(Plan(4), new EstimatorCalibration(), new ResourceBudget { MaxTokens = 10_000_000, MaxCostUsd = 100 }, Prices, Options);

        Assert.Equal(4 * 40_000, e.TokensExpected);
        Assert.Equal(e.TokensExpected / 2, e.TokensLow);
        Assert.Equal(e.TokensExpected * 2, e.TokensHigh);
        // 92% input at $3/M, 8% output at $15/M.
        Assert.Equal(Math.Round(160_000 * (0.92m * 0.000003m + 0.08m * 0.000015m), 4), e.CostUsdExpected);
        Assert.True(e.CostUsdLow < e.CostUsdExpected && e.CostUsdExpected < e.CostUsdHigh);
        Assert.Equal(2 * 90, e.DurationSecondsExpected); // two levels
        Assert.Equal(4, e.TeamSize);
    }

    [Fact]
    public void The_budget_caps_every_high_end()
    {
        var budget = new ResourceBudget { MaxTokens = 100_000, MaxCostUsd = 0.25m, MaxDurationSeconds = 100 };
        var e = CostEstimator.Estimate(Plan(6, depth: 3), new EstimatorCalibration(), budget, Prices, Options);

        Assert.Equal(100_000, e.TokensHigh);
        Assert.Equal(0.25m, e.CostUsdHigh);
        Assert.Equal(100, e.DurationSecondsHigh);
        Assert.True(CostEstimator.CappedByBudget(Plan(6, 3), new EstimatorCalibration(), budget, Prices, Options));
        Assert.False(CostEstimator.CappedByBudget(Plan(1), new EstimatorCalibration(), new ResourceBudget { MaxTokens = 10_000_000, MaxCostUsd = 100 }, Prices, Options));
    }

    [Fact]
    public void Calibration_uses_medians_from_history()
    {
        var cal = CostEstimator.Calibrate(
        [
            new CalibrationSample(Agents: 5, Tokens: 50_000, DurationSeconds: 60, MaxDepth: 2, PlannedTeamSize: 5),
            new CalibrationSample(Agents: 4, Tokens: 20_000, DurationSeconds: 40, MaxDepth: 1, PlannedTeamSize: 2),
            new CalibrationSample(Agents: 2, Tokens: 30_000, DurationSeconds: 30, MaxDepth: 1, PlannedTeamSize: 0),
            new CalibrationSample(Agents: 0, Tokens: 0, DurationSeconds: 5, MaxDepth: 0, PlannedTeamSize: 3) // no agents: ignored
        ]);

        Assert.Equal(3, cal.Samples);
        Assert.Equal("history", cal.Source);
        Assert.Equal(10_000, cal.TokensPerAgent); // median of 10k, 5k, 15k
        Assert.Equal(1.5, cal.TeamSizeFactor);     // median of 1.0 and 2.0 (only tasks with a plan)
        Assert.Equal(20, cal.SecondsPerLevel);     // median of 20, 20, 15

        var e = CostEstimator.Estimate(Plan(2), cal, new ResourceBudget { MaxTokens = 10_000_000, MaxCostUsd = 100 }, Prices, Options);
        Assert.Equal(30_000, e.TokensExpected); // 2 planned × 1.5 × 10k
        Assert.Equal(3, e.TeamSize);
        Assert.Equal(new EstimatorCalibration().Source, CostEstimator.Calibrate([]).Source);
    }

    private static LlmCompletionResponse Proposal(object args) => new()
    {
        ToolCalls = [new ToolCall { Id = "c1", Name = TeamPreviewPrompt.ToolName, ArgumentsJson = JsonSerializer.Serialize(args) }]
    };

    [Fact]
    public void Planning_answer_becomes_a_tree_with_depths()
    {
        var plan = TeamPreviewPrompt.Parse("goal", Proposal(new
        {
            goal_type = "Research",
            team = new object[]
            {
                // A child listed before its parent is still placed under it.
                new { role = "Database Agent", purpose = "Schema", parent_role = "Architect" },
                new { role = "Architect", purpose = "Design" },
                new { role = "Market Researcher", purpose = "Market" },
                new { role = "Orphan", purpose = "x", parent_role = "Nobody" }
            },
            rationale = "why"
        }), maxAgents: 25);

        Assert.Equal("research", plan.GoalType);
        Assert.Equal(5, plan.TeamSize);
        Assert.Equal(2, plan.MaxDepth);
        Assert.Equal(2, plan.Team.Single(t => t.Role == "Database Agent").Depth);
        Assert.Equal("Root Agent", plan.Team.Single(t => t.Role == "Orphan").ParentRole);
    }

    [Fact]
    public void A_bad_or_missing_planning_answer_falls_back_to_the_root_alone()
    {
        Assert.Equal(1, TeamPreviewPrompt.Parse("g", new LlmCompletionResponse { Content = "I'd do it myself." }, 25).TeamSize);
        var garbled = new LlmCompletionResponse { ToolCalls = [new ToolCall { Id = "x", Name = TeamPreviewPrompt.ToolName, ArgumentsJson = "{not json" }] };
        Assert.Equal(1, TeamPreviewPrompt.Parse("g", garbled, 25).TeamSize);
        var huge = Proposal(new { team = Enumerable.Range(0, 100).Select(i => new { role = $"R{i}", purpose = "p" }).ToArray() });
        Assert.Equal(10, TeamPreviewPrompt.Parse("g", huge, maxAgents: 10).TeamSize);
    }

    [Fact]
    public void Planning_prompt_states_the_limits_and_offers_one_tool()
    {
        var request = TeamPreviewPrompt.Build("Build a CRM", new ResourceBudget { MaxChildren = 4, MaxTokens = 123_456, MaxCostUsd = 7 },
            new RuntimeLimitsOptions { MaxAgentDepth = 3, MaxChildrenPerAgent = 6 }, "m", 900);

        Assert.Equal(TeamPreviewPrompt.ToolName, Assert.Single(request.Tools).Name);
        var system = request.Messages[0].Content!;
        Assert.Contains("at most 4 agents", system);
        Assert.Contains("depth at most 3", system);
        Assert.Contains("123,456", system);
        Assert.Equal(900, request.MaxTokens);
    }
}
