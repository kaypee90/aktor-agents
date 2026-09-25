using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.Infrastructure.Memory;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Infrastructure.Secrets;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.LLM;
using AgentRuntime.Memory;
using AgentRuntime.Simulation;
using AgentRuntime.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentRuntime.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntimeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolsOptions>(configuration.GetSection(ToolsOptions.SectionName));

        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        // Registered as a factory (safe for singleton consumers like tools) plus a conventional
        // scoped DbContext sourced from that same factory (for controllers, hosted-service scopes).
        services.AddDbContextFactory<AgentDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<AgentDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContext());

        services.AddSingleton<IMemoryStore, PostgresMemoryStore>();
        services.AddSingleton<IWorldArchive, EfWorldArchive>();
        services.AddSingleton<AgentRuntime.Workspaces.IWorkspaceArchive, EfWorkspaceArchive>();
        services.AddHostedService<PersistenceEventSubscriber>();

        services.AddHttpClient("agent-tools")
            .ConfigurePrimaryHttpMessageHandler(PublicNetworkHandler.Create);

        // Integrations (docs/plugins.md). Their HTTP client isn't SSRF-guarded like agent-tools:
        // integration endpoints are chosen by the user when connecting, never by an agent.
        services.AddHttpClient("integrations", c => c.Timeout = TimeSpan.FromSeconds(60));
        services.Configure<SecretsOptions>(configuration.GetSection(SecretsOptions.SectionName));
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<AgentRuntime.Integrations.ISecretStore, PostgresSecretStore>();
        services.AddSingleton<AgentRuntime.Safety.IAuditLog, Persistence.PostgresAuditLog>();

        // Platform: accounts and access (docs/platform.md), and billing if a provider is configured.
        services.Configure<Identity.AuthOptions>(configuration.GetSection(Identity.AuthOptions.SectionName));
        services.AddSingleton<Identity.IdentityService>();
        if (string.Equals(configuration["Billing:Provider"], "stripe", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<Billing.IBillingProvider, Billing.StripeBillingProvider>();
        }
        else
        {
            services.AddSingleton<Billing.IBillingProvider, Billing.NoBillingProvider>();
        }

        services.AddSingleton<Billing.BillingService>();
        services.AddSingleton<AgentRuntime.Plugins.IAgentPlugin, McpPlugin>();
        services.AddSingleton<AgentRuntime.Plugins.IAgentPlugin, HttpApiPlugin>();
        services.AddSingleton<AgentRuntime.Plugins.IAgentPlugin, SlackPlugin>();
        services.AddSingleton<AgentRuntime.Plugins.IAgentPlugin, TwilioSmsPlugin>();
        services.AddSingleton<AgentRuntime.Plugins.IAgentPlugin, EmailSmtpPlugin>();
        services.AddSingleton<AgentRuntime.Plugins.IAgentPlugin, TelegramPlugin>();
        services.AddPluginsFromDirectory(configuration["Plugins:Directory"] ?? "./plugins");
        RegisterLlmProvider(services, configuration);

        services.AddSingleton<ITool, WebSearchTool>();
        services.AddSingleton<ITool, FilesystemReadTool>();
        services.AddSingleton<ITool, FilesystemWriteTool>();
        services.AddSingleton<ITool, FilesystemListTool>();
        services.AddSingleton<ITool, ShellExecTool>();
        services.AddSingleton<ITool, HttpRequestTool>();
        services.AddSingleton<AgentDatabaseSandbox>();
        services.AddSingleton<OrleansSchemaInstaller>();
        services.AddSingleton<ITool, DatabaseQueryTool>();

        return services;
    }

    private static void RegisterLlmProvider(IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration.GetSection(LlmOptions.SectionName)["Provider"] ?? "Mock";
        // appsettings.json ships "BaseUrl": "" as a documented-empty placeholder, not null — a
        // plain `?? fallback` never catches that, so this must explicitly treat blank as unset.
        var configuredBaseUrl = configuration[$"{LlmOptions.SectionName}:BaseUrl"];

        services.AddHttpClient<AnthropicProvider>(client =>
        {
            client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(configuredBaseUrl) ? "https://api.anthropic.com/" : configuredBaseUrl);
        });
        services.AddHttpClient<OpenAIProvider>(client =>
        {
            client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(configuredBaseUrl) ? "https://api.openai.com/" : configuredBaseUrl);
        });

        services.AddHttpClient<OllamaProvider>(client =>
        {
            // Inside a container "localhost" is the container itself; the .NET base images set
            // DOTNET_RUNNING_IN_CONTAINER, so default to the Docker host there instead.
            var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
            var fallback = inContainer ? "http://host.docker.internal:11434/" : "http://localhost:11434/";
            var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl) ? fallback : configuredBaseUrl;
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(30, configuration.GetValue($"{LlmOptions.SectionName}:TimeoutSeconds", 300)));
        });

        if (providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            // Local inference costs nothing per token. Without this the default (cloud) prices
            // would report phantom spend, and the per-agent cost caps would stop agents early.
            // Explicitly configured prices still win.
            services.PostConfigure<LlmOptions>(o =>
            {
                if (configuration[$"{LlmOptions.SectionName}:PricePerInputTokenUsd"] is null) o.PricePerInputTokenUsd = 0;
                if (configuration[$"{LlmOptions.SectionName}:PricePerOutputTokenUsd"] is null) o.PricePerOutputTokenUsd = 0;
            });
        }

        switch (providerName)
        {
            case "Anthropic":
                services.AddSingleton<ILLMProvider>(sp => sp.GetRequiredService<AnthropicProvider>());
                break;
            case "OpenAI":
                services.AddSingleton<ILLMProvider>(sp => sp.GetRequiredService<OpenAIProvider>());
                break;
            case "Ollama":
                services.AddSingleton<ILLMProvider>(sp => sp.GetRequiredService<OllamaProvider>());
                break;
            case "Gemini":
                services.AddSingleton<ILLMProvider, GeminiProvider>();
                break;
            default:
                services.AddSingleton<ILLMProvider, HeuristicMockLlmProvider>();
                break;
        }
    }

    /// <summary>Applies pending EF Core migrations and provisions the database_query sandbox role at
    /// startup, so `docker compose up` works with no manual step.</summary>
    public static async Task MigrateDatabaseAsync(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        await db.Database.MigrateAsync();

        // Orleans' durable storage tables must exist before the silo starts (it starts after this).
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var orleans = configuration.GetOrleansOptions();
        if (orleans.UsesAdoNetStorage || orleans.UsesAdoNetClustering)
        {
            await host.Services.GetRequiredService<OrleansSchemaInstaller>().InstallAsync(
                configuration.GetConnectionString("Postgres")!, includeClustering: orleans.UsesAdoNetClustering);
        }

        // After migrations, so the tables the sandbox role is locked out of already exist.
        await host.Services.GetRequiredService<AgentDatabaseSandbox>().ProvisionAsync();
    }
}
