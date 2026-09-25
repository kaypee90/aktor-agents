using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// Every tool schema the runtime can send to an LLM must be valid JSON Schema for an object.
/// Providers parse schemas before sending, so one malformed schema breaks every LLM call of every
/// agent that has the tool — and scripted test LLMs never parse them, so only this catches it.
/// </summary>
public class ToolSchemaTests
{
    private static ServiceProvider Services()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = "Host=unused" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRuntimeCore(config);
        services.AddAgentRuntimeInfrastructure(config);
        // Tools only store the grain factory; nothing here calls it.
        services.AddSingleton(System.Reflection.DispatchProxy.Create<IGrainFactory, UnusedProxy>());
        return services.BuildServiceProvider();
    }

    public class UnusedProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Not used by schema tests.");
    }

    private static void AssertValidObjectSchema(string owner, string schema)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(schema);
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException($"{owner}: schema is not valid JSON ({ex.Message})");
        }

        Assert.True(node is JsonObject { } o && o["type"]?.GetValue<string>() == "object", $"{owner}: schema must be a JSON object schema");
    }

    [Fact]
    public async Task EveryRegisteredTool_HasAValidSchema_AndAProviderSafeName()
    {
        await using var sp = Services();
        var tools = sp.GetServices<ITool>().ToList();
        Assert.True(tools.Count > 25, $"expected the full tool set, found {tools.Count}");

        foreach (var tool in tools)
        {
            AssertValidObjectSchema(tool.Definition.Name, tool.Definition.JsonSchema);
            Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", tool.Definition.Name);
        }

        Assert.Equal(tools.Count, tools.Select(t => t.Definition.Name).Distinct().Count());
    }

    [Fact]
    public async Task BuiltInPluginTools_HaveValidSchemas()
    {
        await using var sp = Services();
        var connection = new PluginConnection
        {
            ConnectionId = "c",
            WorkspaceId = "ws",
            Name = "x",
            Settings = new Dictionary<string, string> { ["base_url"] = "https://api.example.com", ["allow_writes"] = "true" },
            Secrets = new Dictionary<string, string>()
        };

        foreach (var plugin in sp.GetServices<IAgentPlugin>().OfType<IToolProviderPlugin>().Where(p => p.Manifest.Id != "mcp"))
        {
            foreach (var tool in await plugin.ListToolsAsync(connection, default))
            {
                AssertValidObjectSchema($"{plugin.Manifest.Id}.{tool.Name}", tool.JsonSchema);
            }
        }
    }
}
