using AgentRuntime.Contracts;

namespace AgentRuntime.Resources;

public enum BudgetStep
{
    /// <summary>Plenty left: keep working.</summary>
    Continue,
    /// <summary>Most of the budget is spent: tell the agent to start finishing.</summary>
    WrapUp,
    /// <summary>Only enough left for one more call: it must be the agent's report (complete_task).</summary>
    FinalStep,
    /// <summary>Not even one more call fits: the runtime reports what the agent has on its behalf.</summary>
    Stop
}

public readonly record struct BudgetCheck(BudgetStep Step, string Reason);

/// <summary>
/// Decides, before each LLM call of an agent with a lifetime budget, whether it can keep working.
/// Running out used to fail the agent and throw its work away; instead the runtime warns it early,
/// keeps back enough budget for one last call in which it reports what it has, and only if even
/// that doesn't fit does it report on the agent's behalf. Either way the parent gets a result
/// (possibly partial, with the remaining work listed), never a bare failure.
/// </summary>
public static class BudgetGuard
{
    public static BudgetCheck Evaluate(
        ResourceBudget budget,
        ResourceUsage usage,
        double elapsedSeconds,
        int nextCallTokens,
        decimal nextCallCostUsd,
        double wrapUpFraction)
    {
        var tokensLeft = budget.RemainingTokens(usage);
        var costLeft = budget.RemainingCostUsd(usage);

        if (tokensLeft <= 0 || tokensLeft < nextCallTokens) return new(BudgetStep.Stop, "token budget");
        if (costLeft <= 0 || costLeft < nextCallCostUsd) return new(BudgetStep.Stop, "cost budget");

        // Enough for one call but not for one more after it: this call is the last.
        if (tokensLeft < 2L * nextCallTokens) return new(BudgetStep.FinalStep, "token budget");
        if (costLeft < 2 * nextCallCostUsd) return new(BudgetStep.FinalStep, "cost budget");
        // complete_task is exempt from the tool-call budget, so the final step can still report.
        if (budget.RemainingToolCalls(usage) <= 0) return new(BudgetStep.FinalStep, "tool-call budget");
        if (elapsedSeconds >= budget.MaxDurationSeconds) return new(BudgetStep.FinalStep, "time budget");

        // A few calls from the end, or past the threshold on any dimension: start wrapping up.
        if (tokensLeft < 4L * nextCallTokens) return new(BudgetStep.WrapUp, "token budget");
        if (Used(usage.TokensUsed + usage.ReservedTokens, budget.MaxTokens) >= wrapUpFraction) return new(BudgetStep.WrapUp, "token budget");
        if (Used(usage.ToolCallsUsed + usage.ReservedToolCalls, budget.MaxToolCalls) >= wrapUpFraction) return new(BudgetStep.WrapUp, "tool-call budget");
        if (Used((double)(usage.CostUsd + usage.ReservedCostUsd), (double)budget.MaxCostUsd) >= wrapUpFraction) return new(BudgetStep.WrapUp, "cost budget");
        if (Used(elapsedSeconds, budget.MaxDurationSeconds) >= wrapUpFraction) return new(BudgetStep.WrapUp, "time budget");

        return new(BudgetStep.Continue, string.Empty);
    }

    private static double Used(double used, double max) => max <= 0 ? 1 : used / max;
}
