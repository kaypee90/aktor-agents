using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// Exercises spawn-limit validation (CLAUDE.md section 10) directly against the grain class,
/// bypassing Orleans hosting since AgentRegistryGrain's logic never touches grain-context APIs.
/// </summary>
public class AgentRegistryGrainTests
{
    private static AgentRegistryGrain CreateGrain(RuntimeLimitsOptions limits, FakePersistentState<RegistryState>? state = null) =>
        new(state ?? new FakePersistentState<RegistryState>(), Options.Create(limits), NullLogger<AgentRegistryGrain>.Instance);

    [Fact]
    public async Task ValidateSpawnAsync_NoParent_RootAllowedAtDepthZero()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions());

        var result = await grain.ValidateSpawnAsync(null);

        Assert.True(result.Allowed);
        Assert.Equal(0, result.AllowedDepth);
    }

    [Fact]
    public async Task ValidateSpawnAsync_UnknownParent_Rejected()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions());

        var result = await grain.ValidateSpawnAsync("does-not-exist");

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task ValidateSpawnAsync_ExceedsMaxDepth_Rejected()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions { MaxAgentDepth = 1 });
        await grain.RegisterAsync(Entry("root", depth: 0, parent: null));
        await grain.RegisterAsync(Entry("child", depth: 1, parent: "root"));

        // child is already at the max depth of 1; spawning from child would be depth 2.
        var result = await grain.ValidateSpawnAsync("child");

        Assert.False(result.Allowed);
        Assert.Contains("depth", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSpawnAsync_ExceedsMaxChildrenPerAgent_Rejected()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions { MaxChildrenPerAgent = 2 });
        await grain.RegisterAsync(Entry("root", depth: 0, parent: null));
        await grain.RegisterAsync(Entry("child-1", depth: 1, parent: "root"));
        await grain.RegisterAsync(Entry("child-2", depth: 1, parent: "root"));

        var result = await grain.ValidateSpawnAsync("root");

        Assert.False(result.Allowed);
        Assert.Contains("children", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSpawnAsync_ExceedsMaxTotalAgents_Rejected()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions { MaxTotalAgents = 1 });
        await grain.RegisterAsync(Entry("root", depth: 0, parent: null));

        var result = await grain.ValidateSpawnAsync(null);

        Assert.False(result.Allowed);
        Assert.Contains("total agent", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSpawnAsync_ExceedsMaxActiveAgents_Rejected()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions { MaxActiveAgents = 1, MaxTotalAgents = 10 });
        await grain.RegisterAsync(Entry("root", depth: 0, parent: null, status: AgentStatus.Executing));

        var result = await grain.ValidateSpawnAsync(null);

        Assert.False(result.Allowed);
        Assert.Contains("active agent", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSpawnAsync_DuplicateRoleUnderSameParent_Rejected()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions());
        await grain.RegisterAsync(Entry("root", 0, null));
        await grain.RegisterAsync(Entry("agent-1", 1, "root") with { Role = "Web Search Specialist" });

        var result = await grain.ValidateSpawnAsync("root", role: "Web Search Specialist");

        Assert.False(result.Allowed);
        Assert.Contains("agent-1", result.RejectionReason);
    }

    [Fact]
    public async Task ValidateSpawnAsync_DuplicateRoleIsCaseInsensitive_Rejected()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions());
        await grain.RegisterAsync(Entry("root", 0, null));
        await grain.RegisterAsync(Entry("agent-1", 1, "root") with { Role = "Web Search Specialist" });

        var result = await grain.ValidateSpawnAsync("root", role: "web search specialist");

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task ValidateSpawnAsync_DifferentRoleUnderSameParent_Allowed()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions());
        await grain.RegisterAsync(Entry("root", 0, null));
        await grain.RegisterAsync(Entry("agent-1", 1, "root") with { Role = "Web Search Specialist" });

        var result = await grain.ValidateSpawnAsync("root", role: "Technical Architecture Agent");

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task ValidateSpawnAsync_SameRoleUnderDifferentParent_Allowed()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions());
        await grain.RegisterAsync(Entry("root", 0, null));
        await grain.RegisterAsync(Entry("agent-1", 1, "root") with { Role = "Detail Agent" });
        await grain.RegisterAsync(Entry("agent-2", 1, "root") with { Role = "Other Agent" });

        // "Detail Agent" spawned under a *different* parent (agent-2) is not a duplicate sibling.
        var result = await grain.ValidateSpawnAsync("agent-2", role: "Detail Agent");

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task FindAsync_FiltersByCapabilityAndStatus()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions());
        await grain.RegisterAsync(Entry("a1", 0, null, status: AgentStatus.Idle, capabilities: ["postgresql"]));
        await grain.RegisterAsync(Entry("a2", 0, null, status: AgentStatus.Completed, capabilities: ["postgresql"]));
        await grain.RegisterAsync(Entry("a3", 0, null, status: AgentStatus.Idle, capabilities: ["web-search"]));

        var results = await grain.FindAsync(new FindAgentsQuery { Capabilities = ["postgresql"], Status = AgentStatus.Idle });

        Assert.Single(results);
        Assert.Equal("a1", results[0].AgentId);
    }

    [Fact]
    public async Task UnregisterAsync_RemovesAgentSoItNoLongerCountsTowardLimits()
    {
        var grain = CreateGrain(new RuntimeLimitsOptions { MaxTotalAgents = 1 });
        await grain.RegisterAsync(Entry("root", 0, null));
        await grain.UnregisterAsync("root");

        var result = await grain.ValidateSpawnAsync(null);

        Assert.True(result.Allowed);
    }

    private static AgentDirectoryEntry Entry(
        string id, int depth, string? parent, AgentStatus status = AgentStatus.Idle, List<string>? capabilities = null) => new()
    {
        AgentId = id,
        Role = "test-role",
        Goal = "test-goal",
        Status = status,
        Capabilities = capabilities ?? [],
        ParentAgentId = parent,
        Depth = depth,
        RootAgentId = parent is null ? id : "root"
    };
}
