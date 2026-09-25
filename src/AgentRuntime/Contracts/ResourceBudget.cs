namespace AgentRuntime.Contracts;

/// <summary>
/// Immutable resource budget granted to an agent. Budgets propagate to children and can only
/// ever be narrowed, never expanded, by the receiving agent (CLAUDE.md section 24).
/// </summary>
[GenerateSerializer]
public sealed record ResourceBudget
{
    [Id(0)] public int MaxTokens { get; init; } = 50_000;
    [Id(1)] public int MaxDurationSeconds { get; init; } = 900;
    [Id(2)] public int MaxChildren { get; init; } = 5;
    [Id(3)] public int MaxToolCalls { get; init; } = 100;
    [Id(4)] public decimal MaxCostUsd { get; init; } = 2.00m;

    /// <summary>When above 0, token/tool-call/cost limits apply per period of this many hours and
    /// usage resets each period (standing agents); 0 means the limits are for the agent's lifetime.</summary>
    [Id(5)] public int PeriodHours { get; init; }

    public int RemainingTokens(ResourceUsage usage) => Math.Max(0, MaxTokens - usage.TokensUsed - usage.ReservedTokens);
    public int RemainingToolCalls(ResourceUsage usage) => Math.Max(0, MaxToolCalls - usage.ToolCallsUsed - usage.ReservedToolCalls);
    public decimal RemainingCostUsd(ResourceUsage usage) => Math.Max(0, MaxCostUsd - usage.CostUsd - usage.ReservedCostUsd);

    /// <summary>
    /// Produces the budget a spawned child may receive. Tokens, tool calls, and cost are pooled:
    /// whatever a child is granted is reserved against the parent (see
    /// <see cref="ResourceUsage.ReservedTokens"/>), so a whole tree can never spend more than the
    /// root's budget (CLAUDE.md section 24). Without a request, the child gets an equal share of
    /// what's left, split across the parent's remaining child slots plus one share the parent keeps
    /// for its own work. An explicit request can take more, up to everything remaining. Duration is
    /// wall-clock rather than pooled, so a child simply can't outlive its parent's deadline.
    /// </summary>
    public ResourceBudget DeriveChildBudget(ResourceUsage parentUsage, ResourceBudget? requested)
    {
        var remainingTokens = RemainingTokens(parentUsage);
        var remainingToolCalls = RemainingToolCalls(parentUsage);
        var remainingCost = RemainingCostUsd(parentUsage);
        var remainingSeconds = Math.Max(0, MaxDurationSeconds - parentUsage.ElapsedSeconds);

        var shares = Math.Max(1, MaxChildren - parentUsage.ChildrenSpawned) + 1;

        return new ResourceBudget
        {
            MaxTokens = Math.Min(requested?.MaxTokens ?? remainingTokens / shares, remainingTokens),
            MaxDurationSeconds = Math.Min(requested?.MaxDurationSeconds ?? remainingSeconds, remainingSeconds),
            MaxChildren = Math.Min(requested?.MaxChildren ?? MaxChildren, MaxChildren),
            MaxToolCalls = Math.Min(requested?.MaxToolCalls ?? remainingToolCalls / shares, remainingToolCalls),
            MaxCostUsd = Math.Min(requested?.MaxCostUsd ?? Math.Round(remainingCost / shares, 4), remainingCost)
        };
    }

    /// <summary>True if this budget can't fund even one LLM turn — spawning an agent with it would
    /// just produce an agent that fails "budget exhausted" on its first iteration.</summary>
    public bool IsEmpty => MaxTokens <= 0 || MaxToolCalls <= 0 || MaxCostUsd <= 0 || MaxDurationSeconds <= 0;
}

[GenerateSerializer]
public sealed record ResourceUsage
{
    [Id(0)] public int TokensUsed { get; init; }
    [Id(1)] public int ToolCallsUsed { get; init; }
    [Id(2)] public int ChildrenSpawned { get; init; }
    [Id(3)] public decimal CostUsd { get; init; }
    [Id(4)] public int ElapsedSeconds { get; init; }

    /// <summary>Budget handed to children, held against this agent's own limits so parent + all
    /// descendants together stay within this agent's budget.</summary>
    [Id(5)] public int ReservedTokens { get; init; }
    [Id(6)] public int ReservedToolCalls { get; init; }
    [Id(7)] public decimal ReservedCostUsd { get; init; }

    /// <summary>Usage from completed budget periods (renewing budgets only), so totals stay visible.</summary>
    [Id(8)] public long LifetimeTokens { get; init; }
    [Id(9)] public long LifetimeToolCalls { get; init; }
    [Id(10)] public decimal LifetimeCostUsd { get; init; }
}

public enum BudgetKind
{
    Tokens,
    Duration,
    Children,
    ToolCalls,
    Cost
}

public sealed class BudgetExceededException(BudgetKind kind, string agentId)
    : Exception($"Agent '{agentId}' exceeded its {kind} budget.")
{
    public BudgetKind Kind { get; } = kind;
    public string AgentId { get; } = agentId;
}
