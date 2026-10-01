using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// A child's tools and permissions are always a subset of its parent's (docs/guarantees.md): asking
/// for capabilities the parent doesn't have ("shell", "postgresql") grants nothing extra, at any depth.
/// </summary>
public sealed class PermissionInheritanceTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private static ToolCall Spawn(string role) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = "spawn_agent",
        ArgumentsJson = JsonSerializer.Serialize(new
        {
            role,
            goal = $"{role}: do privileged things",
            capabilities = new[] { "shell", "docker", "postgresql", "http", "security-scanner", "filesystem" },
            why_not_myself = "Needs its own specialist working in parallel."
        })
    };

    [Fact]
    public async Task Children_never_get_tools_or_permissions_their_parent_lacks()
    {
        ScriptedLlmProviderRegistry.Current = r =>
        {
            var role = PromptInspector.ExtractRole(r.Messages[0].Content!) ?? string.Empty;
            var spawned = r.Messages.Any(m => m.Role == ChatRole.Tool && m.ToolName == "spawn_agent");
            if (!spawned && (role.Contains("Root") || role.Contains("Middle")))
            {
                return new LlmCompletionResponse { ToolCalls = [Spawn(role.Contains("Root") ? "Middle" : "Leaf")], FinishReason = LlmFinishReason.ToolCalls };
            }

            return new LlmCompletionResponse { Content = "Waiting.", FinishReason = LlmFinishReason.Stop };
        };

        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0);
        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        await registry.RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = "g", RootAgentId = rootId });
        var rootPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages | ToolPermission.ReadFilesystem | ToolPermission.WriteFilesystem;
        await _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId).Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = "g",
            AllowedTools = Tools.AgentToolCatalog.ResolveToolsForCapabilities(["research", "filesystem"]).ToList(),
            GrantedPermissions = rootPermissions,
            Budget = new ResourceBudget(),
            TaskId = Guid.NewGuid().ToString("n"),
            AutoStart = true
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        IReadOnlyList<AgentDirectoryEntry> team = [];
        while (DateTime.UtcNow < deadline && team.Count < 3)
        {
            team = await registry.FindAsync(new FindAgentsQuery { RootAgentId = rootId });
            await Task.Delay(50);
        }

        Assert.Equal(3, team.Count);
        var root = await _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId).GetSnapshot();
        foreach (var entry in team.Where(a => a.AgentId != rootId))
        {
            var child = await _cluster.GrainFactory.GetGrain<IAgentGrain>(entry.AgentId).GetSnapshot();
            Assert.DoesNotContain("shell_exec", child.AllowedTools);
            Assert.DoesNotContain("database_query", child.AllowedTools);
            Assert.DoesNotContain("http_request", child.AllowedTools);
            Assert.Equal(ToolPermission.None, child.GrantedPermissions & ~root.GrantedPermissions);
            Assert.All(child.AllowedTools.Except(Tools.AgentToolCatalog.GovernanceTools), t => Assert.Contains(t, root.AllowedTools));
        }
    }
}
