using AgentRuntime.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace AgentRuntime.Tenancy;

[GenerateSerializer]
public sealed class TenantGrainState
{
    [Id(0)] public TenantBillingState Billing { get; set; } = new();
    [Id(1)] public TenantUsagePeriod Current { get; set; } = new();
    [Id(2)] public List<TenantUsagePeriod> History { get; set; } = [];
    [Id(3)] public HashSet<string> ParkedAgents { get; set; } = [];
}

public sealed class TenantGrain(
    [PersistentState("tenant", "Default")] IPersistentState<TenantGrainState> state,
    IOptions<BillingOptions> billingOptions,
    ILogger<TenantGrain> logger) : Grain, ITenantGrain
{
    private const int MaxHistory = 24;
    private const int MaxParked = 1000;

    private TenantGrainState S => state.State;
    private string TenantId => this.GetPrimaryKeyString();

    private PlanDefinition Plan => billingOptions.Value.Plan(string.IsNullOrEmpty(S.Billing.PlanId) ? null : S.Billing.PlanId);

    public Task<PlanDefinition> GetPlan() => Task.FromResult(Plan);

    public Task<QuotaDecision> CheckQuota()
    {
        var plan = Plan;
        var period = TenantUsagePeriod.PeriodOf(DateTimeOffset.UtcNow);
        // A new month starts from zero even before anything has been recorded in it.
        var tokens = S.Current.Period == period ? S.Current.Tokens : 0;
        var cost = S.Current.Period == period ? S.Current.CostUsd : 0;

        if (plan.MonthlyTokenLimit > 0 && tokens >= plan.MonthlyTokenLimit)
        {
            return Task.FromResult(new QuotaDecision
            {
                Allowed = false,
                Reason = $"the organization used its {plan.Name} plan's {plan.MonthlyTokenLimit:N0} tokens for this month"
            });
        }

        if (plan.MonthlyCostLimitUsd > 0 && cost >= plan.MonthlyCostLimitUsd)
        {
            return Task.FromResult(new QuotaDecision
            {
                Allowed = false,
                Reason = $"the organization used its {plan.Name} plan's ${plan.MonthlyCostLimitUsd:F2} of model usage for this month"
            });
        }

        return Task.FromResult(QuotaDecision.Ok);
    }

    public async Task RecordUsage(UsageDelta delta)
    {
        RollPeriodIfDue();
        var c = S.Current;
        c.Tokens += delta.Tokens;
        c.CostUsd += delta.CostUsd;
        c.LlmCalls += delta.LlmCalls;
        c.ToolCalls += delta.ToolCalls;
        c.AgentsCreated += delta.AgentsCreated;
        await state.WriteStateAsync();
    }

    public async Task ParkForQuota(string agentId)
    {
        if (S.ParkedAgents.Count >= MaxParked || !S.ParkedAgents.Add(agentId)) return;
        await state.WriteStateAsync();
    }

    public async Task<TenantUsageView> GetUsage()
    {
        if (RollPeriodIfDue()) await state.WriteStateAsync();
        return new TenantUsageView
        {
            TenantId = TenantId,
            Plan = Plan,
            Billing = S.Billing with { PlanId = Plan.Id },
            Current = S.Current,
            History = S.History.ToList(),
            Quota = await CheckQuota(),
            ParkedAgents = S.ParkedAgents.Count
        };
    }

    public async Task SetBilling(TenantBillingState billing)
    {
        var before = Plan.Id;
        S.Billing = billing;
        await state.WriteStateAsync();
        logger.LogInformation("Tenant {TenantId} billing updated: plan {Before} -> {After} ({Status})",
            TenantId, before, Plan.Id, billing.SubscriptionStatus);
        await WakeParkedAgentsAsync();
    }

    /// <summary>Starts a new month. Agents parked on last month's quota find it renewed on their
    /// next recovery check; waking them here just makes that immediate.</summary>
    private bool RollPeriodIfDue()
    {
        var period = TenantUsagePeriod.PeriodOf(DateTimeOffset.UtcNow);
        if (S.Current.Period == period) return false;

        if (!string.IsNullOrEmpty(S.Current.Period))
        {
            S.History.Insert(0, S.Current);
            if (S.History.Count > MaxHistory) S.History.RemoveRange(MaxHistory, S.History.Count - MaxHistory);
        }

        S.Current = new TenantUsagePeriod { Period = period };
        return true;
    }

    private async Task WakeParkedAgentsAsync()
    {
        if (S.ParkedAgents.Count == 0 || !(await CheckQuota()).Allowed) return;

        var agents = S.ParkedAgents.ToList();
        S.ParkedAgents.Clear();
        await state.WriteStateAsync();
        foreach (var agentId in agents)
        {
            try
            {
                await GrainFactory.GetGrain<IAgentGrain>(agentId).WakeAndThink();
            }
            catch (Exception ex)
            {
                // It will find the quota renewed on its own recovery check anyway.
                logger.LogWarning(ex, "Couldn't wake parked agent {AgentId} for tenant {TenantId}", agentId, TenantId);
            }
        }
    }
}
