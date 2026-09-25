using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Billing;
using AgentRuntime.Infrastructure.Identity;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentRuntime.Tests;

public sealed class CredentialTests
{
    [Fact]
    public void Passwords_VerifyOnlyThemselves_AndHashesAreSalted()
    {
        var hash = PasswordHasher.Hash("correct horse battery", iterations: 1_000);
        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("correct horse batterY", hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("correct horse battery", iterations: 1_000));
        Assert.DoesNotContain("correct horse", hash);
    }

    [Fact]
    public void OldHashes_AreFlaggedForRehash_AndGarbageNeverVerifies()
    {
        Assert.True(PasswordHasher.NeedsRehash(PasswordHasher.Hash("x", iterations: 1_000)));
        Assert.False(PasswordHasher.Verify("x", "plain-text"));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2-sha256$1000$not-base64$zzz"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public void WeakPasswords_AreRejected(string? password) => Assert.NotNull(PasswordHasher.Validate(password));

    [Fact]
    public void ApiKeys_RoundTrip_AndOnlyTheirHashIsNeeded()
    {
        var (keyId, key) = ApiKeyFormat.New();
        Assert.StartsWith("ak_" + keyId + "_", key);
        Assert.True(ApiKeyFormat.TryParse(key, out var parsed));
        Assert.Equal(keyId, parsed);
        Assert.True(SecretTokens.HashEquals(key, SecretTokens.Hash(key)));
        Assert.False(SecretTokens.HashEquals(key + "x", SecretTokens.Hash(key)));
        Assert.DoesNotContain(key[^20..], ApiKeyFormat.Display(key));
    }

    [Theory]
    [InlineData("sk_live_123")]
    [InlineData("ak_short_x")]
    [InlineData("ak_ABCDEF123456_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(null)]
    public void MalformedKeys_AreNotParsed(string? value) => Assert.False(ApiKeyFormat.TryParse(value, out _));
}

public sealed class StripeTests
{
    private const string Secret = "whsec_test";

    [Fact]
    public void Signature_IsAccepted_OnlyWhenGenuineAndFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = """{"id":"evt_1","type":"ping","data":{"object":{}}}""";
        var t = now.ToUnixTimeSeconds();
        var header = $"t={t},v1={StripeBillingProvider.Sign(payload, t, Secret)}";

        Assert.True(StripeBillingProvider.VerifySignature(payload, header, Secret, now));
        Assert.False(StripeBillingProvider.VerifySignature(payload + " ", header, Secret, now));             // body changed
        Assert.False(StripeBillingProvider.VerifySignature(payload, header, "whsec_other", now));            // wrong secret
        Assert.False(StripeBillingProvider.VerifySignature(payload, header, Secret, now.AddMinutes(10)));    // replayed later
        Assert.False(StripeBillingProvider.VerifySignature(payload, $"t={t}", Secret, now));                 // no signature
        Assert.True(StripeBillingProvider.VerifySignature(payload, $"t={t},v1=deadbeef,{header.Split(',')[1]}", Secret, now)); // rotated secrets
    }

    [Fact]
    public void SubscriptionEvents_AreReducedToTenantPlanAndStatus()
    {
        var provider = new StripeBillingProvider(null!, Options.Create(new BillingOptions { Stripe = new StripeOptions { WebhookSecret = Secret } }));
        var payload = """
        {"id":"evt_9","type":"customer.subscription.updated","data":{"object":{
          "id":"sub_1","customer":"cus_1","status":"active","metadata":{"tenant_id":"t-abc","plan_id":"pro"},
          "items":{"data":[{"price":{"id":"price_pro"},"current_period_end":1790000000}]}}}}
        """;
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var evt = provider.ParseWebhook(payload, $"t={t},v1={StripeBillingProvider.Sign(payload, t, Secret)}");

        Assert.NotNull(evt);
        Assert.Equal("t-abc", evt.TenantId);
        Assert.Equal("cus_1", evt.CustomerId);
        Assert.Equal("active", evt.SubscriptionStatus);
        Assert.Equal("price_pro", evt.PriceId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), evt.CurrentPeriodEnd);
        Assert.Null(provider.ParseWebhook(payload, "t=1,v1=00"));
    }
}

public sealed class TenancyUnitTests
{
    [Fact]
    public void Plans_FallBackToTheDefault_AndUnlimitedAlwaysExists()
    {
        var options = new BillingOptions { DefaultPlan = "free", Plans = [new PlanDefinition { Id = "free", MonthlyTokenLimit = 10 }] };
        Assert.Equal("free", options.Plan(null).Id);
        Assert.Equal("free", options.Plan("gone").Id);
        Assert.Contains(options.AllPlans(), p => p.Id == "unlimited" && p.MonthlyTokenLimit == 0);
        Assert.Equal("unlimited", new BillingOptions().Plan(null).Id);
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("t-1", "t-1")]
    public void TenantIds_Normalize(string? input, string expected) => Assert.Equal(expected, TenantIds.Normalize(input));

    [Theory]
    [InlineData("t-1a2b3c4d5e", "t_1a2b3c4d5e")]
    [InlineData("T-X';DROP", "t_x__drop")]
    public void SandboxSchemaSuffixes_AreSafeIdentifiers(string tenant, string expected) =>
        Assert.Equal(expected, AgentDatabaseSandbox.TenantSuffix(tenant));

    private static AgentRegistryGrain Registry(RuntimeLimitsOptions limits) =>
        new(new FakePersistentState<RegistryState>(), Options.Create(limits), NullLogger<AgentRegistryGrain>.Instance);

    private static AgentDirectoryEntry Entry(string id, string tenant, string? parent = null) => new()
    {
        AgentId = id, Role = "r-" + id, Goal = "g", Status = AgentStatus.Executing, ParentAgentId = parent, TenantId = tenant, RootAgentId = parent ?? id
    };

    [Fact]
    public async Task AgentLimits_AreCountedPerOrganization()
    {
        var registry = Registry(new RuntimeLimitsOptions { MaxTotalAgents = 2, MaxActiveAgents = 2 });
        await registry.RegisterAsync(Entry("a1", "t-a"));
        await registry.RegisterAsync(Entry("a2", "t-a"));

        Assert.False((await registry.ValidateSpawnAsync(null, tenantId: "t-a")).Allowed);
        Assert.True((await registry.ValidateSpawnAsync(null, tenantId: "t-b")).Allowed); // another tenant isn't affected
    }

    [Fact]
    public async Task PlanActiveAgentLimit_AppliesOnTop()
    {
        var registry = Registry(new RuntimeLimitsOptions());
        await registry.RegisterAsync(Entry("a1", "t-a"));
        var result = await registry.ValidateSpawnAsync(null, tenantId: "t-a", tenantMaxActive: 1);
        Assert.False(result.Allowed);
        Assert.Contains("plan allows 1 active agents", result.RejectionReason);
    }

    [Fact]
    public async Task AParentFromAnotherOrganization_IsUnknown()
    {
        var registry = Registry(new RuntimeLimitsOptions());
        await registry.RegisterAsync(Entry("a1", "t-a"));
        var result = await registry.TryRegisterSpawnAsync(Entry("b1", "t-b", parent: "a1"));
        Assert.False(result.Allowed);
        Assert.Contains("Unknown parent", result.RejectionReason);
    }

    [Fact]
    public async Task Discovery_IsFilteredByOrganization()
    {
        var registry = Registry(new RuntimeLimitsOptions());
        await registry.RegisterAsync(Entry("a1", "t-a"));
        await registry.RegisterAsync(Entry("b1", "t-b"));
        var found = await registry.FindAsync(new FindAgentsQuery { TenantId = "t-a" });
        Assert.Equal(["a1"], found.Select(a => a.AgentId));
    }
}
