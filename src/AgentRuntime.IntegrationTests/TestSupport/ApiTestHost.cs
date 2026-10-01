using System.Net.Http.Headers;
using System.Security.Cryptography;
using AgentRuntime.Infrastructure.Identity;
using AgentRuntime.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>
/// The real API (controllers, auth, MCP, A2A, ACP, persistence, the co-hosted silo) running
/// in-process against a throwaway Postgres container with pgvector, using the Mock LLM provider.
/// Needs Docker; when it isn't available the tests using this host are skipped, not failed.
/// Never touches a developer's own stack: its own container, random port, in-memory grain storage.
/// </summary>
public sealed class ApiTestHost : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>Why the host couldn't start (no Docker), or null when it's running.</summary>
    public string? Unavailable { get; private set; }

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException(Unavailable);
    public string ConnectionString => _postgres!.GetConnectionString();

    /// <summary>Extra configuration for a test class (applied over the defaults below).</summary>
    public Dictionary<string, string?> Settings { get; } = [];

    /// <summary>Extra service registrations (e.g. a deterministic embedding provider).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
                .WithDatabase("aktor_test")
                .WithUsername("aktor")
                .WithPassword("aktor_test_password")
                .Build();
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            Unavailable = $"Docker isn't available for the API tests' Postgres container: {ex.Message}";
            return;
        }

        var siloPort = Random.Shared.Next(21000, 29000);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = ConnectionString,
            ["Tools:DatabaseConnectionString"] = ConnectionString,
            ["Tools:WorkspaceRoot"] = Path.Combine(Path.GetTempPath(), "aktor-api-tests", Guid.NewGuid().ToString("n")),
            ["Silo:Storage"] = "Memory",
            ["Silo:Clustering"] = "Localhost",
            ["Silo:SiloPort"] = siloPort.ToString(),
            ["Silo:GatewayPort"] = (siloPort + 1000).ToString(),
            ["Llm:Provider"] = "Mock",
            ["Auth:Mode"] = "accounts",
            ["Auth:AllowSignup"] = "true",
            ["Secrets:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Dashboard:BaseUrl"] = "http://dashboard.test",
            ["Tasks:Callbacks:AllowPrivateNetworks"] = "true",
            ["Tasks:Callbacks:SweepInterval"] = "00:00:01",
            ["Plugins:Directory"] = Path.Combine(Path.GetTempPath(), "aktor-api-tests-no-plugins"),
            ["Logging:LogLevel:Default"] = "Warning"
        };
        foreach (var (k, v) in Settings) settings[k] = v;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            // UseSetting, not ConfigureAppConfiguration: Program reads its configuration (the
            // connection string among it) while building, before late configuration sources apply.
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            if (ConfigureServices is { } configure) b.ConfigureServices(configure);
        });

        // Never let a misapplied setting point the tests at a developer's own database: the settings
        // that reach outside the process are also set as environment variables while the host
        // starts, which WebApplication.CreateBuilder reads before anything else.
        string[] critical = ["ConnectionStrings:Postgres", "Tools:DatabaseConnectionString", "Silo:Storage", "Silo:SiloPort", "Silo:GatewayPort"];
        var previous = critical.ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k.Replace(":", "__")));
        foreach (var key in critical) Environment.SetEnvironmentVariable(key.Replace(":", "__"), settings[key]);
        IConfiguration configuration;
        try
        {
            configuration = _factory.Services.GetRequiredService<IConfiguration>(); // starts the host
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key.Replace(":", "__"), value);
        }

        var configured = configuration.GetConnectionString("Postgres");
        if (configured != ConnectionString)
        {
            throw new InvalidOperationException($"The API test host isn't using its throwaway database (got '{configured}').");
        }
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    /// <summary>A new organization (via sign-up) and an API key for it.</summary>
    public async Task<Organization> CreateOrganizationAsync(string name, TenantRole keyRole = TenantRole.Member)
    {
        using var scope = Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var email = $"{name.ToLowerInvariant()}-{Guid.NewGuid():n}@example.test";
        var signedUp = await identity.SignUpAsync(email, "correct-horse-battery-staple-1", name, name, null, CancellationToken.None);
        if (signedUp.Value is null) throw new InvalidOperationException(signedUp.Error);

        var caller = new Caller(signedUp.Value.TenantId, TenantRole.Owner, signedUp.Value.User.UserId, email, null, false, null);
        var key = await identity.CreateApiKeyAsync(caller, $"{name} test key", keyRole, null, CancellationToken.None);
        if (key.Error is not null) throw new InvalidOperationException(key.Error);
        return new Organization(signedUp.Value.TenantId, key.Value.Secret);
    }

    public HttpClient ClientFor(Organization org)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", org.ApiKey);
        return client;
    }
}

public sealed record Organization(string TenantId, string ApiKey);
