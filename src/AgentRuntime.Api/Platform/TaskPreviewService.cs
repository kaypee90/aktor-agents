using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.LLM;
using AgentRuntime.Resources;
using AgentRuntime.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

public sealed record TaskPreviewView
{
    public required string PreviewId { get; init; }
    public required string Goal { get; init; }
    public string? GoalType { get; init; }
    public required IReadOnlyList<PlannedTeamMemberView> Team { get; init; }
    public int TeamSize { get; init; }
    public int MaxDepth { get; init; }
    public string? Rationale { get; init; }
    public required TaskEstimate Estimate { get; init; }
    public required ResourceBudget Budget { get; init; }
    public bool CappedByBudget { get; init; }
    public bool RequiresConfirmation { get; init; }
    public decimal ConfirmAboveUsd { get; init; }
    public required CalibrationView Calibration { get; init; }
    public required PlanningCostView Planning { get; init; }
}

public sealed record PlannedTeamMemberView(string Role, string Purpose, int Depth, string? ParentRole);
public sealed record CalibrationView(int Samples, double TokensPerAgent, double TeamSizeFactor, string Source);
public sealed record PlanningCostView(int Tokens, decimal CostUsd);

/// <summary>
/// Cost and team preview before a run (roadmap P2): one cheap planning call sketches the team the
/// root agent would likely build, and the estimate turns it into token, dollar and time ranges,
/// calibrated on the organization's own recent tasks. The planning call is metered like any other
/// and refused when the organization is over quota. Previews are kept, so the task started from one
/// records its estimate and the result can report estimate vs actual.
/// </summary>
public sealed class TaskPreviewService(
    ILLMProvider llm,
    IAgentPromptBuilder prompts,
    IGrainFactory grains,
    IDbContextFactory<AgentDbContext> dbFactory,
    IOptions<LlmOptions> llmOptions,
    IOptions<RuntimeLimitsOptions> limits,
    IOptions<PreviewOptions> options,
    IOptions<DefaultBudgetOptions> defaultBudget,
    IOptions<TaskBudgetCeilingOptions> ceiling,
    ILogger<TaskPreviewService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<TaskPreviewView> PreviewAsync(string tenantId, string goal, ResourceBudget? requested, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new TaskServiceException("goal is required");
        var budget = ceiling.Value.Clamp(requested ?? defaultBudget.Value.ToBudget());

        var tenant = grains.GetGrain<ITenantGrain>(TenantIds.Normalize(tenantId));
        var quota = await tenant.CheckQuota();
        if (!quota.Allowed) throw new TaskServiceException(quota.Reason ?? "The organization's plan limit is reached.", StatusCodes.Status429TooManyRequests);

        var opts = options.Value;
        var prices = llmOptions.Value;
        var request = prompts.BuildTeamPreviewRequest(goal, budget, limits.Value, prices.Model, opts.PlanningMaxOutputTokens);

        TeamPlan plan;
        var planningTokens = 0;
        var planningCost = 0m;
        try
        {
            var response = await llm.CompleteAsync(request, ct);
            planningTokens = response.InputTokens + response.OutputTokens;
            planningCost = prices.CostOf(response);
            await tenant.RecordUsage(new UsageDelta { Tokens = planningTokens, CostUsd = planningCost, LlmCalls = 1 });
            plan = TeamPreviewPrompt.Parse(goal, response, opts.MaxPlannedAgents);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The preview is advice: if the planning call fails, estimate from a root-only team
            // (the budget still caps the high end) rather than blocking the task.
            logger.LogWarning(ex, "Preview planning call failed; estimating without a planned team");
            plan = new TeamPlan { Goal = goal, Team = [new PlannedRole("Root Agent", "Works on the goal.", 0, null)], Rationale = "Planning call failed: " + ex.Message };
        }

        var calibration = await CalibrateAsync(tenantId, ct);
        var estimate = CostEstimator.Estimate(plan, calibration, budget, prices, opts);

        var previewId = "pv-" + Guid.NewGuid().ToString("n")[..16];
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.TaskPreviews.Add(new TaskPreviewRecord
            {
                PreviewId = previewId,
                TenantId = tenantId,
                Goal = goal,
                PlanJson = JsonSerializer.Serialize(plan, Json),
                EstimateJson = JsonSerializer.Serialize(estimate, Json),
                BudgetJson = JsonSerializer.Serialize(budget, Json),
                PlanningTokens = planningTokens,
                PlanningCostUsd = planningCost,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        return new TaskPreviewView
        {
            PreviewId = previewId,
            Goal = goal,
            GoalType = plan.GoalType,
            Team = plan.Team.Select(t => new PlannedTeamMemberView(t.Role, t.Purpose, t.Depth, t.ParentRole)).ToList(),
            TeamSize = plan.TeamSize,
            MaxDepth = plan.MaxDepth,
            Rationale = plan.Rationale,
            Estimate = estimate,
            Budget = budget,
            CappedByBudget = CostEstimator.CappedByBudget(plan, calibration, budget, prices, opts),
            RequiresConfirmation = estimate.CostUsdHigh > opts.ConfirmAboveUsd,
            ConfirmAboveUsd = opts.ConfirmAboveUsd,
            Calibration = new CalibrationView(calibration.Samples, Math.Round(calibration.TokensPerAgent), Math.Round(calibration.TeamSizeFactor, 2), calibration.Source),
            Planning = new PlanningCostView(planningTokens, Math.Round(planningCost, 6))
        };
    }

    /// <summary>The organization's recent finished tasks: agents, tokens, duration, depth, and the
    /// team size their preview planned (if they had one).</summary>
    private async Task<EstimatorCalibration> CalibrateAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recent = await db.Tasks.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.CompletedAt != null && t.ReplayOfTaskId == null && t.Status != "Rejected")
            .OrderByDescending(t => t.CompletedAt)
            .Take(options.Value.CalibrationTasks)
            .Select(t => new { t.TaskId, t.CreatedAt, t.CompletedAt, t.EstimateJson })
            .ToListAsync(ct);
        if (recent.Count == 0) return new EstimatorCalibration();

        var ids = recent.Select(t => t.TaskId).ToList();
        var agents = await db.Agents.AsNoTracking().Where(a => ids.Contains(a.TaskId))
            .GroupBy(a => a.TaskId)
            .Select(g => new { TaskId = g.Key, Count = g.Count(), Tokens = g.Sum(a => (long)a.TokensUsed), MaxDepth = g.Max(a => a.Depth) })
            .ToDictionaryAsync(g => g.TaskId, ct);

        var samples = recent.Where(t => agents.ContainsKey(t.TaskId)).Select(t =>
        {
            var a = agents[t.TaskId];
            var planned = t.EstimateJson is null ? 0 : JsonSerializer.Deserialize<TaskEstimate>(t.EstimateJson, Json)?.TeamSize ?? 0;
            return new CalibrationSample(a.Count, a.Tokens, (t.CompletedAt!.Value - t.CreatedAt).TotalSeconds, a.MaxDepth, planned);
        }).ToList();
        return CostEstimator.Calibrate(samples);
    }

    /// <summary>The estimate of a preview this organization made, for the task started from it.</summary>
    public async Task<string?> ClaimEstimateAsync(string tenantId, string previewId, string taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var preview = await db.TaskPreviews.FirstOrDefaultAsync(p => p.PreviewId == previewId && p.TenantId == tenantId, ct);
        if (preview is null) return null;
        preview.TaskId ??= taskId;
        await db.SaveChangesAsync(ct);
        return preview.EstimateJson;
    }
}
