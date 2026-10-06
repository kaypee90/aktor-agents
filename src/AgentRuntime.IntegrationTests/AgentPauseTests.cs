using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// An agent's snapshot says whether a person paused it, so the dashboard offers Resume only while
/// it is paused and Pause otherwise.
/// </summary>
public sealed class AgentPauseTests : IAsyncLifetime
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
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    [Fact]
    public async Task The_snapshot_shows_a_pause_until_resume()
    {
        var tenant = "org-" + Guid.NewGuid().ToString("n")[..6];
        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        await _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0)
            .RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = "g", RootAgentId = rootId, TenantId = tenant });
        var root = _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId);
        await root.Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = "Summarise the quarter.",
            AllowedTools = ["complete_task"],
            Budget = new ResourceBudget(),
            TaskId = Guid.NewGuid().ToString("n"),
            TenantId = tenant
        });

        Assert.False((await root.GetSnapshot()).Paused);

        await root.Pause();
        Assert.True(await WaitForAsync(root, s => s.Paused), "the pause shows in the snapshot");

        await root.Resume();
        Assert.True(await WaitForAsync(root, s => !s.Paused), "resume clears it");
    }

    private static async Task<bool> WaitForAsync(IAgentGrain agent, Func<AgentSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition(await agent.GetSnapshot())) return true;
            await Task.Delay(50);
        }

        return false;
    }
}
