using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Integrations;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// The MCP plugin against a real MCP server. Skipped unless MCP_TEST_URL points at a Streamable
/// HTTP server, e.g. the reference server:
///   PORT=3005 npx -y @modelcontextprotocol/server-everything streamableHttp
///   MCP_TEST_URL=http://localhost:3005/mcp dotnet test --filter McpLiveTests
/// </summary>
public class McpLiveTests
{
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static readonly string? Url = Environment.GetEnvironmentVariable("MCP_TEST_URL");

    [Fact]
    public async Task ConnectsListsAndCallsTools_OnARealMcpServer()
    {
        if (string.IsNullOrEmpty(Url)) return; // not configured: nothing to check against

        await using var plugin = new McpPlugin(new Factory(), Options.Create(new IntegrationsOptions()), NullLoggerFactory.Instance);
        var connection = new PluginConnection
        {
            ConnectionId = "conn-live",
            WorkspaceId = "ws-live",
            Name = "everything",
            Settings = new Dictionary<string, string> { ["transport"] = "http", ["url"] = Url },
            Secrets = new Dictionary<string, string>()
        };

        var check = await plugin.ValidateAsync(connection, default);
        Assert.True(check.Ok, check.Message);

        var tools = await plugin.ListToolsAsync(connection, default);
        Assert.NotEmpty(tools);
        var echo = tools.FirstOrDefault(t => t.Name == "echo");
        Assert.NotNull(echo);

        var result = await plugin.ExecuteToolAsync(connection, "echo", new ToolExecutionRequest
        {
            ToolName = "everything__echo",
            AgentId = "agent-1",
            TaskId = "ws-live",
            ArgumentsJson = """{"message":"hello from aktor"}"""
        });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("hello from aktor", result.ResultJson);
    }
}
