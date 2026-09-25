using System.Security.Cryptography;
using Orleans.Concurrency;

namespace AgentRuntime.Tenancy;

/// <summary>
/// Tenant (organization) ids. Every agent, workspace, world, task, event and memory record belongs
/// to exactly one tenant, stamped by the runtime when it's created and inherited by everything it
/// creates; nothing an agent says can change it (docs/platform.md).
/// </summary>
public static class TenantIds
{
    /// <summary>The tenant of single-user installs and of everything created before multi-tenancy.</summary>
    public const string Default = "default";

    public static string New() => "t-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

    /// <summary>Records written before tenancy existed have no tenant: they belong to the default one.</summary>
    public static string Normalize(string? tenantId) => string.IsNullOrWhiteSpace(tenantId) ? Default : tenantId;

    public static bool Same(string? a, string? b) => Normalize(a) == Normalize(b);
}

/// <summary>A member's role in an organization. Ordered: each role can do everything the ones
/// before it can.</summary>
public enum TenantRole
{
    Viewer = 0,
    Member = 1,
    Admin = 2,
    Owner = 3
}

/// <summary>What a plan allows. Zero means unlimited.</summary>
[GenerateSerializer]
public sealed record PlanDefinition
{
    [Id(0)] public string Id { get; init; } = "unlimited";
    [Id(1)] public string Name { get; init; } = "Unlimited";
    [Id(2)] public string Description { get; init; } = string.Empty;
    [Id(3)] public long MonthlyTokenLimit { get; init; }
    [Id(4)] public decimal MonthlyCostLimitUsd { get; init; }
    [Id(5)] public int MaxWorkspaces { get; init; }
    [Id(6)] public int MaxActiveAgents { get; init; }
    [Id(7)] public int MaxMembers { get; init; }
    /// <summary>Shown on the plans page; what the customer pays is whatever the Stripe price says.</summary>
    [Id(8)] public decimal PriceMonthlyUsd { get; init; }
    /// <summary>The Stripe price a checkout for this plan subscribes to. Empty: not purchasable.</summary>
    [Id(9)] public string? StripePriceId { get; init; }
}

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>The plan new organizations start on. Self-hosted installs default to unlimited.</summary>
    public string DefaultPlan { get; set; } = "unlimited";

    /// <summary>The plan an organization falls back to when its paid subscription ends.</summary>
    public string LapsedPlan { get; set; } = string.Empty;

    /// <summary>"none" (self-hosted) or "stripe".</summary>
    public string Provider { get; set; } = "none";

    public List<PlanDefinition> Plans { get; set; } = [];

    public StripeOptions Stripe { get; set; } = new();

    public IReadOnlyList<PlanDefinition> AllPlans() =>
        Plans.Any(p => p.Id == "unlimited") ? Plans : [new PlanDefinition(), .. Plans];

    public PlanDefinition Plan(string? id) =>
        AllPlans().FirstOrDefault(p => p.Id == id)
        ?? AllPlans().FirstOrDefault(p => p.Id == DefaultPlan)
        ?? new PlanDefinition();

    public string FallbackPlanId => string.IsNullOrWhiteSpace(LapsedPlan) ? DefaultPlan : LapsedPlan;
}

public sealed class StripeOptions
{
    public string SecretKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    /// <summary>Where the dashboard lives; checkout and the billing portal return here.</summary>
    public string AppBaseUrl { get; set; } = "http://localhost:3000";
    public string ApiBaseUrl { get; set; } = "https://api.stripe.com";
}

/// <summary>One month of an organization's metered usage.</summary>
[GenerateSerializer]
public sealed record TenantUsagePeriod
{
    /// <summary>UTC calendar month, "yyyy-MM".</summary>
    [Id(0)] public string Period { get; init; } = string.Empty;
    [Id(1)] public long Tokens { get; set; }
    [Id(2)] public decimal CostUsd { get; set; }
    [Id(3)] public long LlmCalls { get; set; }
    [Id(4)] public long ToolCalls { get; set; }
    [Id(5)] public long AgentsCreated { get; set; }

    public static string PeriodOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM");
}

[GenerateSerializer]
public sealed record UsageDelta
{
    [Id(0)] public long Tokens { get; init; }
    [Id(1)] public decimal CostUsd { get; init; }
    [Id(2)] public long LlmCalls { get; init; }
    [Id(3)] public long ToolCalls { get; init; }
    [Id(4)] public long AgentsCreated { get; init; }
}

[GenerateSerializer]
public sealed record QuotaDecision
{
    [Id(0)] public bool Allowed { get; init; } = true;
    [Id(1)] public string? Reason { get; init; }

    public static readonly QuotaDecision Ok = new();
}

[GenerateSerializer]
public sealed record TenantBillingState
{
    [Id(0)] public string PlanId { get; init; } = string.Empty;
    /// <summary>none, active, trialing, past_due, canceled...: as the billing provider reports it.</summary>
    [Id(1)] public string SubscriptionStatus { get; init; } = "none";
    [Id(2)] public string? CustomerId { get; init; }
    [Id(3)] public string? SubscriptionId { get; init; }
    [Id(4)] public DateTimeOffset? CurrentPeriodEnd { get; init; }
}

[GenerateSerializer]
public sealed record TenantUsageView
{
    [Id(0)] public required string TenantId { get; init; }
    [Id(1)] public required PlanDefinition Plan { get; init; }
    [Id(2)] public required TenantBillingState Billing { get; init; }
    [Id(3)] public required TenantUsagePeriod Current { get; init; }
    [Id(4)] public List<TenantUsagePeriod> History { get; init; } = [];
    [Id(5)] public QuotaDecision Quota { get; init; } = QuotaDecision.Ok;
    [Id(6)] public int ParkedAgents { get; init; }
}

/// <summary>
/// One organization's metering and plan enforcement (key: tenant id). Agents check the quota before
/// every LLM call and report what each call cost; an agent over quota parks and is woken here when
/// the plan changes (or finds the quota renewed at the start of the month on its own).
/// </summary>
public interface ITenantGrain : IGrainWithStringKey
{
    [AlwaysInterleave]
    Task<QuotaDecision> CheckQuota();

    [AlwaysInterleave]
    Task<PlanDefinition> GetPlan();

    Task RecordUsage(UsageDelta delta);

    /// <summary>An agent stopped for lack of quota; it's woken when the plan changes.</summary>
    Task ParkForQuota(string agentId);

    Task<TenantUsageView> GetUsage();

    /// <summary>Applies what the billing provider (or an operator) says the organization is on.</summary>
    Task SetBilling(TenantBillingState billing);
}
