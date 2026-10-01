using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Tests;

/// <summary>Roadmap P3: every team-shape rule, enforced by the runtime rather than by prompting.</summary>
public sealed class TeamPolicyTests
{
    private static AgentDirectoryEntry Agent(string id, string role, string goal, string? parent = null, int depth = 0,
        AgentStatus status = AgentStatus.Thinking, params string[] capabilities) => new()
    {
        AgentId = id,
        Role = role,
        Goal = goal,
        ParentAgentId = parent,
        Depth = depth,
        Status = status,
        RootAgentId = "root",
        Capabilities = [.. capabilities]
    };

    private static readonly AgentDirectoryEntry Root = Agent("root", "Root Agent", "Research the market for an AI property management SaaS", capabilities: "orchestration");

    private static SpawnValidationResult Validate(TeamPolicy policy, AgentDirectoryEntry parent, IReadOnlyCollection<AgentDirectoryEntry> team,
        string role = "Analyst", string goal = "Analyze pricing models", string? rootGoal = null) =>
        TeamShapeValidator.Validate([policy], parent, team, role, goal, rootGoal ?? Root.Goal);

    private static JsonElement Details(SpawnValidationResult r) => JsonDocument.Parse(r.DetailsJson!).RootElement;

    [Fact]
    public void Max_agents_caps_the_whole_team()
    {
        var team = new[] { Root, Agent("a", "A", "x", "root", 1), Agent("b", "B", "y", "root", 1) };
        var r = Validate(new TeamPolicy { MaxAgents = 3, PreventDuplicateRoles = false }, Root, team);

        Assert.False(r.Allowed);
        Assert.Equal(TeamRules.MaxAgents, r.Rule);
        Assert.Equal(3, Details(r).GetProperty("limit").GetInt32());
        Assert.True(Validate(new TeamPolicy { MaxAgents = 4 }, Root, team).Allowed);
    }

    [Fact]
    public void Goal_type_limit_applies_by_keyword_or_explicit_type()
    {
        var policy = new TeamPolicy
        {
            GoalTypes =
            [
                new GoalTypeLimit { GoalType = "research", Keywords = ["research", "market analysis"], MaxAgents = 2 },
                new GoalTypeLimit { GoalType = "coding", Keywords = ["implement"], MaxAgents = 10 }
            ]
        };
        var team = new[] { Root, Agent("a", "A", "x", "root", 1) };

        var byKeyword = Validate(policy, Root, team);
        Assert.False(byKeyword.Allowed);
        Assert.Equal(TeamRules.MaxAgentsForGoalType, byKeyword.Rule);
        Assert.Equal("research", Details(byKeyword).GetProperty("goal_type").GetString());

        // The caller's own goal type wins over keyword matching.
        Assert.True(TeamShapeValidator.Validate([policy, new TeamPolicy { GoalType = "coding" }], Root, team, "Analyst", "Analyze", Root.Goal).Allowed);

        // No matching type: no type limit.
        Assert.True(Validate(policy, Root, team, rootGoal: "Write a poem").Allowed);
        Assert.Equal("research", TeamShapeValidator.Classify(policy, "Do a quick MARKET analysis, please"));
        Assert.Null(TeamShapeValidator.Classify(policy, "Analysis of markets")); // whole words only
    }

    [Fact]
    public void Only_listed_roles_or_capabilities_may_spawn()
    {
        var policy = new TeamPolicy { SpawnerRoles = ["Root Agent", "*architect*"], SpawnerCapabilities = ["planning"] };
        var researcher = Agent("r", "Research Agent", "x", "root", 1);
        var architect = Agent("t", "Technical Architect", "y", "root", 1);
        var planner = Agent("p", "Planner", "z", "root", 1, AgentStatus.Thinking, "planning");
        var team = new[] { Root, researcher, architect, planner };

        Assert.True(Validate(policy, Root, team).Allowed);
        Assert.True(Validate(policy, architect, team).Allowed);
        Assert.True(Validate(policy, planner, team).Allowed);
        var refused = Validate(policy, researcher, team);
        Assert.False(refused.Allowed);
        Assert.Equal(TeamRules.SpawnerNotAllowed, refused.Rule);
    }

    [Fact]
    public void Fan_out_is_limited_per_level_with_the_last_entry_for_deeper_levels()
    {
        var policy = new TeamPolicy { MaxFanOutByDepth = [3, 1], PreventDuplicateRoles = false };
        var child = Agent("c1", "C", "x", "root", 1);
        var grandchild = Agent("g1", "G", "y", "c1", 2);
        var team = new List<AgentDirectoryEntry> { Root, child, Agent("c2", "C2", "x", "root", 1), grandchild };

        Assert.True(Validate(policy, Root, team).Allowed);           // root: 2 of 3
        team.Add(Agent("c3", "C3", "x", "root", 1));
        var rootFull = Validate(policy, Root, team);                  // root: 3 of 3
        Assert.Equal(TeamRules.FanOut, rootFull.Rule);
        Assert.Equal(0, Details(rootFull).GetProperty("depth").GetInt32());

        Assert.Equal(TeamRules.FanOut, Validate(policy, child, team).Rule); // depth 1: 1 of 1
        Assert.True(Validate(policy, grandchild, team).Allowed);            // depth 2 uses the last entry (1): 0 of 1
        Assert.Contains("may not start", Validate(new TeamPolicy { MaxFanOutByDepth = [0] }, Root, [Root]).RejectionReason);
    }

    [Fact]
    public void Duplicate_role_and_goal_is_refused_while_the_twin_is_live()
    {
        var policy = new TeamPolicy();
        var twin = Agent("m1", "Market Research Specialist", "Research the market size for property management software", "root", 1);
        var team = new[] { Root, twin };

        var refused = Validate(policy, Root, team, "market researcher agent", "Research the market size of property management software");
        Assert.False(refused.Allowed);
        Assert.Equal(TeamRules.DuplicateRole, refused.Rule);
        Assert.Equal("m1", refused.ExistingAgentId);
        Assert.Equal("m1", Details(refused).GetProperty("existing_agent_id").GetString());

        // Same role, different work: allowed.
        Assert.True(Validate(policy, Root, team, "Market Research Specialist", "Survey landlords about tenant screening pain points").Allowed);
        // Different role, same work: allowed (the goal alone doesn't make a duplicate).
        Assert.True(Validate(policy, Root, team, "Financial Analyst", "Research the market size for property management software").Allowed);
        // A finished twin doesn't block a new one.
        Assert.True(Validate(policy, Root, [Root, twin with { Status = AgentStatus.Completed }], "market researcher",
            "Research the market size for property management software").Allowed);
        // And the rule can be switched off.
        Assert.True(Validate(new TeamPolicy { PreventDuplicateRoles = false }, Root, team, "market researcher",
            "Research the market size for property management software").Allowed);
    }

    [Fact]
    public void An_agent_cannot_clone_itself()
    {
        var self = Agent("r1", "Research Agent", "Research competitors in property management", "root", 1);
        var r = Validate(new TeamPolicy(), self, [Root, self], "Research Agent", "Research competitors in property management");
        Assert.Equal(TeamRules.DuplicateRole, r.Rule);
    }

    [Fact]
    public void Every_policy_must_allow_so_a_task_can_only_tighten_the_server()
    {
        var server = new TeamPolicy { MaxAgents = 10 };
        var task = new TeamPolicy { MaxAgents = 2 };
        var looser = new TeamPolicy { MaxAgents = 50 };
        var team = new[] { Root, Agent("a", "A", "x", "root", 1) };

        Assert.Equal(TeamRules.MaxAgents, TeamShapeValidator.Validate([server, task], Root, team, "B", "y", Root.Goal).Rule);
        var big = Enumerable.Range(0, 10).Select(i => Agent($"x{i}", $"R{i}", $"g{i}", "root", 1)).Prepend(Root).ToList();
        Assert.False(TeamShapeValidator.Validate([server, looser], Root, big, "B", "y", Root.Goal).Allowed);
    }

    [Theory]
    [InlineData("Database Specialist", "database specialists", true)]
    [InlineData("Security Agent", "security", true)]
    [InlineData("Backend-Engineer", "backend engineer agent", true)]
    [InlineData("Backend Engineer", "Frontend Engineer", false)]
    [InlineData("Agent", "Specialist", false)]
    public void Role_equivalence(string a, string b, bool same) => Assert.Equal(same, TeamShapeValidator.SameRole(a, b));

    [Fact]
    public async Task Registry_applies_the_server_and_task_policies_atomically_with_registration()
    {
        var grain = new AgentRegistryGrain(new FakePersistentState<RegistryState>(), Options.Create(new RuntimeLimitsOptions()),
            Options.Create(new TeamPolicy { MaxAgents = 3 }), NullLogger<AgentRegistryGrain>.Instance);
        await grain.RegisterAsync(Root);

        var first = await grain.TryRegisterSpawnAsync(Agent("a", "Analyst", "Analyze pricing", "root", 1), "Analyst");
        Assert.True(first.Allowed);

        // The task's own policy is tighter: no more children for the root.
        var tight = await grain.TryRegisterSpawnAsync(Agent("b", "Writer", "Write the report", "root", 1), "Writer", 0,
            [new TeamPolicy { MaxFanOutByDepth = [1] }]);
        Assert.Equal(TeamRules.FanOut, tight.Rule);
        Assert.Null(await grain.GetAsync("b"));

        var second = await grain.TryRegisterSpawnAsync(Agent("b", "Writer", "Write the report", "root", 1), "Writer");
        Assert.True(second.Allowed);
        var third = await grain.TryRegisterSpawnAsync(Agent("c", "Editor", "Edit the report", "root", 1), "Editor");
        Assert.Equal(TeamRules.MaxAgents, third.Rule);
    }
}
