using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.Infrastructure.Memory;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.LLM;
using AgentRuntime.Memory;
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
        services.AddHostedService<PersistenceEventSubscriber>();

        services.AddHttpClient("agent-tools")
            .ConfigurePrimaryHttpMessageHandler(PublicNetworkHandler.Create);
        RegisterLlmProvider(services, configuration);

        services.AddSingleton<ITool, WebSearchTool>();
        services.AddSingleton<ITool, FilesystemReadTool>();
        services.AddSingleton<ITool, FilesystemWriteTool>();
        services.AddSingleton<ITool, FilesystemListTool>();
        services.AddSingleton<ITool, ShellExecTool>();
        services.AddSingleton<ITool, HttpRequestTool>();
        services.AddSingleton<AgentDatabaseSandbox>();
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

        switch (providerName)
        {
            case "Anthropic":
                services.AddSingleton<ILLMProvider>(sp => sp.GetRequiredService<AnthropicProvider>());
                break;
            case "OpenAI":
                services.AddSingleton<ILLMProvider>(sp => sp.GetRequiredService<OpenAIProvider>());
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

        // After migrations, so the tables the sandbox role is locked out of already exist.
        await host.Services.GetRequiredService<AgentDatabaseSandbox>().ProvisionAsync();
    }
}
