using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Billing;

/// <summary>A verified billing-provider event, reduced to what the platform acts on.</summary>
public sealed record BillingWebhookEvent
{
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public string? TenantId { get; init; }
    public string? CustomerId { get; init; }
    public string? SubscriptionId { get; init; }
    public string? SubscriptionStatus { get; init; }
    public string? PriceId { get; init; }
    public string? PlanId { get; init; }
    public DateTimeOffset? CurrentPeriodEnd { get; init; }
}

/// <summary>Where plans are bought. Self-hosted installs use none: plans are set by an operator.</summary>
public interface IBillingProvider
{
    bool Enabled { get; }

    /// <summary>A hosted checkout page that subscribes the organization to the plan.</summary>
    Task<string> CreateCheckoutUrlAsync(TenantRecord tenant, PlanDefinition plan, string? customerEmail, CancellationToken ct);

    /// <summary>The provider's self-service page (cards, invoices, cancelling).</summary>
    Task<string> CreatePortalUrlAsync(TenantRecord tenant, CancellationToken ct);

    /// <summary>Verifies the webhook's signature; null if it isn't genuine.</summary>
    BillingWebhookEvent? ParseWebhook(string payload, string? signatureHeader);
}

public sealed class NoBillingProvider : IBillingProvider
{
    public bool Enabled => false;
    public Task<string> CreateCheckoutUrlAsync(TenantRecord tenant, PlanDefinition plan, string? customerEmail, CancellationToken ct) =>
        throw new InvalidOperationException("Billing isn't enabled on this server; an operator sets plans.");
    public Task<string> CreatePortalUrlAsync(TenantRecord tenant, CancellationToken ct) =>
        throw new InvalidOperationException("Billing isn't enabled on this server.");
    public BillingWebhookEvent? ParseWebhook(string payload, string? signatureHeader) => null;
}

/// <summary>
/// Stripe Checkout (subscriptions), the Customer Portal, and signed webhooks, over Stripe's REST
/// API. The organization's id travels as <c>client_reference_id</c> and in subscription metadata, so
/// webhooks can always be matched to it.
/// </summary>
public sealed class StripeBillingProvider(IHttpClientFactory httpFactory, IOptions<BillingOptions> options) : IBillingProvider
{
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    private StripeOptions Stripe => options.Value.Stripe;

    public bool Enabled => !string.IsNullOrWhiteSpace(Stripe.SecretKey);

    public async Task<string> CreateCheckoutUrlAsync(TenantRecord tenant, PlanDefinition plan, string? customerEmail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan.StripePriceId)) throw new InvalidOperationException($"The {plan.Name} plan can't be bought online.");
        var app = Stripe.AppBaseUrl.TrimEnd('/');
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "subscription"),
            new("line_items[0][price]", plan.StripePriceId),
            new("line_items[0][quantity]", "1"),
            new("client_reference_id", tenant.TenantId),
            new("metadata[tenant_id]", tenant.TenantId),
            new("metadata[plan_id]", plan.Id),
            new("subscription_data[metadata][tenant_id]", tenant.TenantId),
            new("subscription_data[metadata][plan_id]", plan.Id),
            new("success_url", $"{app}/settings?tab=billing&checkout=success"),
            new("cancel_url", $"{app}/settings?tab=billing&checkout=cancelled")
        };
        if (!string.IsNullOrEmpty(tenant.StripeCustomerId)) form.Add(new("customer", tenant.StripeCustomerId));
        else if (!string.IsNullOrEmpty(customerEmail)) form.Add(new("customer_email", customerEmail));

        using var doc = await PostAsync("/v1/checkout/sessions", form, idempotencyKey: $"checkout-{tenant.TenantId}-{plan.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmm}", ct);
        return doc.RootElement.GetProperty("url").GetString() ?? throw new InvalidOperationException("Stripe returned no checkout URL.");
    }

    public async Task<string> CreatePortalUrlAsync(TenantRecord tenant, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenant.StripeCustomerId)) throw new InvalidOperationException("There's no billing account yet: choose a plan first.");
        using var doc = await PostAsync("/v1/billing_portal/sessions",
        [
            new("customer", tenant.StripeCustomerId),
            new("return_url", $"{Stripe.AppBaseUrl.TrimEnd('/')}/settings?tab=billing")
        ], idempotencyKey: null, ct);
        return doc.RootElement.GetProperty("url").GetString() ?? throw new InvalidOperationException("Stripe returned no portal URL.");
    }

    private async Task<JsonDocument> PostAsync(string path, List<KeyValuePair<string, string>> form, string? idempotencyKey, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("integrations");
        using var request = new HttpRequestMessage(HttpMethod.Post, Stripe.ApiBaseUrl.TrimEnd('/') + path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Stripe.SecretKey);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryError(body) ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Stripe: {message}");
        }

        return JsonDocument.Parse(body);
    }

    private static string? TryError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public BillingWebhookEvent? ParseWebhook(string payload, string? signatureHeader)
    {
        if (!VerifySignature(payload, signatureHeader, Stripe.WebhookSecret, DateTimeOffset.UtcNow)) return null;

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? string.Empty;
        var obj = root.GetProperty("data").GetProperty("object");
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? Meta(string name) => obj.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, name) : null;

        var evt = new BillingWebhookEvent { EventId = Str(root, "id") ?? string.Empty, Type = type };
        return type switch
        {
            "checkout.session.completed" => evt with
            {
                TenantId = Str(obj, "client_reference_id") ?? Meta("tenant_id"),
                CustomerId = Str(obj, "customer"),
                SubscriptionId = Str(obj, "subscription"),
                PlanId = Meta("plan_id")
            },
            "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted" => evt with
            {
                TenantId = Meta("tenant_id"),
                CustomerId = Str(obj, "customer"),
                SubscriptionId = Str(obj, "id"),
                SubscriptionStatus = type == "customer.subscription.deleted" ? "canceled" : Str(obj, "status"),
                PlanId = Meta("plan_id"),
                PriceId = FirstPriceId(obj),
                CurrentPeriodEnd = PeriodEnd(obj)
            },
            _ => evt
        };
    }

    private static string? FirstPriceId(JsonElement subscription)
    {
        if (!subscription.TryGetProperty("items", out var items) || !items.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("price", out var price) && price.TryGetProperty("id", out var id)) return id.GetString();
        }

        return null;
    }

    private static DateTimeOffset? PeriodEnd(JsonElement subscription)
    {
        // Newer API versions moved current_period_end onto the subscription items.
        if (subscription.TryGetProperty("current_period_end", out var end) && end.TryGetInt64(out var s)) return DateTimeOffset.FromUnixTimeSeconds(s);
        if (subscription.TryGetProperty("items", out var items) && items.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("current_period_end", out var e) && e.TryGetInt64(out var t)) return DateTimeOffset.FromUnixTimeSeconds(t);
            }
        }

        return null;
    }

    /// <summary>Stripe's scheme: <c>t=timestamp,v1=hex(HMAC-SHA256(secret, "{t}.{payload}"))</c>, with
    /// a freshness window against replays.</summary>
    public static bool VerifySignature(string payload, string? header, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(secret)) return false;
        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) timestamp = t;
            else if (kv[0].Trim() == "v1") signatures.Add(kv[1].Trim());
        }

        if (timestamp is null || signatures.Count == 0) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).Duration() > SignatureTolerance) return false;

        var expected = Sign(payload, timestamp.Value, secret);
        return signatures.Any(sig => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(expected)));
    }

    public static string Sign(string payload, long timestamp, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
    }
}

/// <summary>Applies plan changes (from the billing provider or an operator) to the organization's
/// record and to the runtime that enforces them.</summary>
public sealed class BillingService(
    IDbContextFactory<AgentDbContext> dbFactory,
    IBillingProvider provider,
    IOptions<BillingOptions> options,
    IGrainFactory grains,
    ILogger<BillingService> logger)
{
    public bool Enabled => provider.Enabled;

    public IReadOnlyList<PlanDefinition> Plans => options.Value.AllPlans();

    /// <summary>The plans to offer an organization. The built-in unlimited plan is internal unless
    /// it's this server's default (self-hosted) or already the organization's plan.</summary>
    public IReadOnlyList<PlanDefinition> PlansFor(string currentPlanId) =>
        Plans.Where(p => p.Id != "unlimited" || options.Value.DefaultPlan == "unlimited" || currentPlanId == "unlimited").ToList();

    /// <summary>Handles a verified webhook once (redeliveries are recognised by event id).</summary>
    public async Task<bool> HandleWebhookAsync(BillingWebhookEvent evt, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.BillingEvents.FindAsync([evt.EventId], ct) is not null) return false;

        var tenant = evt.TenantId is not null
            ? await db.Tenants.FindAsync([evt.TenantId], ct)
            : evt.CustomerId is not null ? await db.Tenants.FirstOrDefaultAsync(t => t.StripeCustomerId == evt.CustomerId, ct) : null;

        if (tenant is not null)
        {
            switch (evt.Type)
            {
                case "checkout.session.completed":
                    tenant.StripeCustomerId = evt.CustomerId ?? tenant.StripeCustomerId;
                    tenant.StripeSubscriptionId = evt.SubscriptionId ?? tenant.StripeSubscriptionId;
                    if (evt.PlanId is { } paidPlan && Plans.Any(p => p.Id == paidPlan))
                    {
                        tenant.PlanId = paidPlan;
                        tenant.SubscriptionStatus = "active";
                    }
                    break;

                case "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted":
                    tenant.StripeCustomerId = evt.CustomerId ?? tenant.StripeCustomerId;
                    tenant.StripeSubscriptionId = evt.SubscriptionId ?? tenant.StripeSubscriptionId;
                    tenant.SubscriptionStatus = evt.SubscriptionStatus ?? tenant.SubscriptionStatus;
                    tenant.CurrentPeriodEnd = evt.CurrentPeriodEnd ?? tenant.CurrentPeriodEnd;
                    // A subscription in good standing grants its plan (past_due too, while the
                    // provider retries the card); anything else falls back.
                    tenant.PlanId = evt.SubscriptionStatus is "active" or "trialing" or "past_due"
                        ? PlanFor(evt) ?? tenant.PlanId
                        : options.Value.FallbackPlanId;
                    break;
            }

            await ApplyAsync(tenant);
        }
        else
        {
            logger.LogWarning("Billing event {EventId} ({Type}) matched no organization", evt.EventId, evt.Type);
        }

        db.BillingEvents.Add(new BillingEventRecord { EventId = evt.EventId, Type = evt.Type, ReceivedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private string? PlanFor(BillingWebhookEvent evt) =>
        Plans.FirstOrDefault(p => p.StripePriceId is { Length: > 0 } price && price == evt.PriceId)?.Id
        ?? (evt.PlanId is { } id && Plans.Any(p => p.Id == id) ? id : null);

    /// <summary>An operator setting a plan directly (self-hosted installs, support, trials).</summary>
    public async Task<bool> SetPlanAsync(string tenantId, string planId, CancellationToken ct)
    {
        if (Plans.All(p => p.Id != planId)) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return false;
        tenant.PlanId = planId;
        await db.SaveChangesAsync(ct);
        await ApplyAsync(tenant);
        return true;
    }

    private Task ApplyAsync(TenantRecord tenant) =>
        grains.GetGrain<ITenantGrain>(tenant.TenantId).SetBilling(new TenantBillingState
        {
            PlanId = tenant.PlanId,
            SubscriptionStatus = tenant.SubscriptionStatus,
            CustomerId = tenant.StripeCustomerId,
            SubscriptionId = tenant.StripeSubscriptionId,
            CurrentPeriodEnd = tenant.CurrentPeriodEnd
        });

    /// <summary>Brings the runtime in line with the stored plan (e.g. for an organization created
    /// before its grain existed).</summary>
    public async Task SyncAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, ct) is { } tenant) await ApplyAsync(tenant);
    }
}
