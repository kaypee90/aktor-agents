using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRuntime.Tests;

public class ToolRegistryTests
{
    private sealed class EchoTool : ITool
    {
        public ToolDefinition Definition { get; } = new()
        {
            Name = "echo",
            Description = "Echoes input back.",
            RequiredPermissions = ToolPermission.None,
            JsonSchema = "{}"
        };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) =>
            Task.FromResult(ToolExecutionResult.Ok(request.ArgumentsJson));
    }

    private sealed class ShellyTool : ITool
    {
        public ToolDefinition Definition { get; } = new()
        {
            Name = "shelly",
            Description = "Pretends to run shell commands.",
            RequiredPermissions = ToolPermission.ExecuteShell,
            JsonSchema = "{}"
        };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) =>
            Task.FromResult(ToolExecutionResult.Ok("{}"));
    }

    private static ToolRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITool, EchoTool>();
        services.AddSingleton<ITool, ShellyTool>();
        var provider = services.BuildServiceProvider();
        return new ToolRegistry(provider);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_Fails()
    {
        var registry = BuildRegistry();

        var result = await registry.ExecuteAsync(
            new ToolExecutionRequest { ToolName = "does_not_exist", AgentId = "a1", TaskId = "t1", ArgumentsJson = "{}" },
            agentAllowedTools: ["echo"],
            agentGrantedPermissions: ToolPermission.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_ToolNotInAgentsAllowedList_Fails()
    {
        var registry = BuildRegistry();

        var result = await registry.ExecuteAsync(
            new ToolExecutionRequest { ToolName = "echo", AgentId = "a1", TaskId = "t1", ArgumentsJson = "{}" },
            agentAllowedTools: ["shelly"], // echo not granted
            agentGrantedPermissions: ToolPermission.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_MissingRequiredPermission_Fails()
    {
        var registry = BuildRegistry();

        var result = await registry.ExecuteAsync(
            new ToolExecutionRequest { ToolName = "shelly", AgentId = "a1", TaskId = "t1", ArgumentsJson = "{}" },
            agentAllowedTools: ["shelly"],
            agentGrantedPermissions: ToolPermission.None); // lacks ExecuteShell

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedAndPermitted_Succeeds()
    {
        var registry = BuildRegistry();

        var result = await registry.ExecuteAsync(
            new ToolExecutionRequest { ToolName = "shelly", AgentId = "a1", TaskId = "t1", ArgumentsJson = "{}" },
            agentAllowedTools: ["shelly"],
            agentGrantedPermissions: ToolPermission.ExecuteShell);

        Assert.True(result.Success);
    }

    [Fact]
    public void ResolveToolsForCapabilities_AlwaysIncludesGovernanceTools()
    {
        var tools = AgentToolCatalog.ResolveToolsForCapabilities([]);

        foreach (var governanceTool in AgentToolCatalog.GovernanceTools)
        {
            Assert.Contains(governanceTool, tools);
        }
    }
}
