using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Memory;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P4 against real Postgres + pgvector: shared knowledge is recalled by meaning, the keyword
/// path still works with no embedding provider, results are ranked with recency, and another
/// organization's entries are never returned however similar they are.
/// </summary>
public sealed class MemorySearchTests(MemorySearchTests.Fixture fixture, ITestOutputHelper output) : IClassFixture<MemorySearchTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public ApiTestHost Host { get; } = new();
        public ConceptEmbeddingProvider Embeddings { get; } = new();

        public Task InitializeAsync()
        {
            Host.ConfigureServices = services => services.AddSingleton<IEmbeddingProvider>(Embeddings);
            return Host.InitializeAsync();
        }

        public Task DisposeAsync() => Host.DisposeAsync();
    }

    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private IMemoryStore Store => Host.Factory.Services.GetRequiredService<IMemoryStore>();

    /// <summary>The same database, searched without any embedding provider.</summary>
    private IMemoryStore KeywordOnlyStore => new PostgresMemoryStore(Host.Factory.Services.GetRequiredService<IDbContextFactory<AgentDbContext>>());

    private static string Tenant() => "t-" + Guid.NewGuid().ToString("n")[..8];

    private static MemoryRecord Shared(string tenant, string key, string value, DateTimeOffset? at = null) => new()
    {
        TenantId = tenant, AgentId = "agent-" + key, Kind = MemoryKind.Shared, Key = key, Value = value,
        CreatedAt = at ?? DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Shared_knowledge_is_recalled_by_meaning()
    {
        if (Skip()) return;
        var tenant = Tenant();
        await Store.WriteAsync(Shared(tenant, "tenant-churn", "Renters vacate mostly because maintenance repairs take weeks."));
        await Store.WriteAsync(Shared(tenant, "billing-notes", "Stripe invoices are verified with an HMAC webhook signature."));
        await Store.WriteAsync(Shared(tenant, "pricing", "Rivals like AppFolio charge per unit per month."));

        // No word of the query appears in the entry it should find.
        var results = await Store.SearchAsync(tenant, "why do occupants leave", MemoryKind.Shared);

        Assert.NotEmpty(results);
        Assert.Equal("tenant-churn", results[0].Key);
        Assert.DoesNotContain(results, r => r.Key == "billing-notes");
        Assert.True(results[0].Score > 0.5, $"score {results[0].Score}");
        output.WriteLine(string.Join("\n", results.Select(r => $"{r.Score:F3} {r.Key}")));
    }

    [Fact]
    public async Task Keyword_search_works_without_an_embedding_provider()
    {
        if (Skip()) return;
        var tenant = Tenant();
        await KeywordOnlyStore.WriteAsync(Shared(tenant, "deploy-log", "The 14:02 deploy of checkout-service raised error rates."));
        await KeywordOnlyStore.WriteAsync(Shared(tenant, "ops-oncall", "On-call rotation changes every Monday."));

        var byWord = await KeywordOnlyStore.SearchAsync(tenant, "deploy errors", MemoryKind.Shared);
        Assert.Equal("deploy-log", Assert.Single(byWord).Key);

        var bySubstring = await KeywordOnlyStore.SearchAsync(tenant, "checkout-serv", MemoryKind.Shared);
        Assert.Equal("deploy-log", Assert.Single(bySubstring).Key);

        // Meaning alone finds nothing here: there are no vectors to compare.
        Assert.Empty(await KeywordOnlyStore.SearchAsync(tenant, "why do occupants leave", MemoryKind.Shared));
    }

    [Fact]
    public async Task Another_organizations_entries_are_never_returned()
    {
        if (Skip()) return;
        var mine = Tenant();
        var theirs = Tenant();
        await Store.WriteAsync(Shared(theirs, "their-churn", "Renters vacate because maintenance repairs take weeks."));
        await Store.WriteAsync(Shared(mine, "my-pricing", "Rivals charge per unit."));

        var results = await Store.SearchAsync(mine, "renters vacate maintenance repairs weeks", MemoryKind.Shared);
        Assert.DoesNotContain(results, r => r.TenantId != mine);
        Assert.Null(await Store.ReadAsync(mine, "any-agent", "their-churn"));
        Assert.Contains(await Store.SearchAsync(theirs, "renters vacate", MemoryKind.Shared), r => r.Key == "their-churn");
    }

    [Fact]
    public async Task Equally_relevant_entries_rank_newest_first()
    {
        if (Skip()) return;
        var tenant = Tenant();
        await Store.WriteAsync(Shared(tenant, "old-finding", "Tenants leave over slow repairs.", DateTimeOffset.UtcNow.AddDays(-120)));
        await Store.WriteAsync(Shared(tenant, "new-finding", "Tenants leave over slow repairs.", DateTimeOffset.UtcNow));

        var results = await Store.SearchAsync(tenant, "residents moving out", MemoryKind.Shared);
        Assert.Equal(["new-finding", "old-finding"], results.Select(r => r.Key).Take(2).ToArray());
    }

    [Fact]
    public async Task Rewriting_an_entry_reembeds_it()
    {
        if (Skip()) return;
        var tenant = Tenant();
        var record = Shared(tenant, "topic", "Stripe invoices and payments.");
        await Store.WriteAsync(record);
        Assert.Equal("topic", (await Store.SearchAsync(tenant, "billing", MemoryKind.Shared)).Single().Key);

        await Store.WriteAsync(record with { Value = "Renters vacate over plumbing." });
        Assert.Empty(await Store.SearchAsync(tenant, "billing", MemoryKind.Shared));
        Assert.Equal("topic", (await Store.SearchAsync(tenant, "occupants leaving", MemoryKind.Shared)).Single().Key);
    }
}
