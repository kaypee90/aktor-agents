using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Memory;
using AgentRuntime.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>Wires the same DI graph the real API uses (<see cref="ServiceCollectionExtensions.AddAgentRuntimeCore"/>),
/// swapping only the LLM provider and memory store for hermetic test doubles.</summary>
public sealed class TestSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("Default");

        siloBuilder.ConfigureServices(services =>
        {
            // Scripted test agents report zero token usage and run with small budgets, so the
            // real-provider funding minimum would reject every spawn these tests exercise.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RuntimeLimits:MinChildTokens"] = "0",
                    ["RuntimeLimits:MinChildToolCalls"] = "1"
                })
                .Build();
            services.AddAgentRuntimeCore(configuration);
            services.AddSingleton<ILLMProvider, ScriptedLlmProvider>();
            services.AddSingleton<IMemoryStore, InMemoryMemoryStore>();

            // Sandboxed workspace tools, so tool-inheritance behaviour matches production.
            services.Configure<ToolsOptions>(o => o.WorkspaceRoot = Path.Combine(Path.GetTempPath(), "aktor-test-workspace"));
            services.AddSingleton<ITool, FilesystemReadTool>();
            services.AddSingleton<ITool, FilesystemWriteTool>();
            services.AddSingleton<ITool, FilesystemListTool>();
        });
    }
}
