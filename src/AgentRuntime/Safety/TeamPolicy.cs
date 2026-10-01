using System.Text.RegularExpressions;
using AgentRuntime.Contracts;

namespace AgentRuntime.Safety;

/// <summary>A cap on team size for one kind of goal, e.g. "research" goals get at most 6 agents.</summary>
[GenerateSerializer]
public sealed record GoalTypeLimit
{
    [Id(0)] public required string GoalType { get; init; }
    /// <summary>Words that mark a goal as this type when the caller didn't say ("research", "analyze").
    /// Matched case-insensitively against whole words of the task's goal.</summary>
    [Id(1)] public List<string> Keywords { get; init; } = [];
    [Id(2)] public int MaxAgents { get; init; }
}

/// <summary>
/// The shape an agent team may take, enforced by the runtime at every spawn (roadmap P3). Team
/// size used to be shaped by prompting alone; these rules hold whatever the model decides. A
/// policy comes from the server's configuration, from the task or workspace it runs in, or both;
/// every policy that applies must allow a spawn, so a task can tighten the server's rules but
/// never loosen them, and children always run under their root's rules.
/// </summary>
[GenerateSerializer]
public sealed record TeamPolicy
{
    public const string SectionName = "TeamPolicy";

    /// <summary>Most agents in the whole team (the root included); null = no limit from this policy.</summary>
    [Id(0)] public int? MaxAgents { get; init; }

    /// <summary>Team-size caps by kind of goal.</summary>
    [Id(1)] public List<GoalTypeLimit> GoalTypes { get; init; } = [];

    /// <summary>The task's goal type, when the caller states it; otherwise it's worked out from
    /// <see cref="GoalTypeLimit.Keywords"/>.</summary>
    [Id(2)] public string? GoalType { get; init; }

    /// <summary>Roles allowed to spawn (globs, e.g. "Root Agent", "*architect*"). Empty: any role.</summary>
    [Id(3)] public List<string> SpawnerRoles { get; init; } = [];

    /// <summary>Capabilities that allow an agent to spawn (globs). Empty: any. With both lists set,
    /// an agent matching either may spawn.</summary>
    [Id(4)] public List<string> SpawnerCapabilities { get; init; } = [];

    /// <summary>Most children an agent at each depth may have: entry 0 is the root, entry 1 its
    /// children, and so on; the last entry applies to every deeper level. Empty: no limit.</summary>
    [Id(5)] public List<int> MaxFanOutByDepth { get; init; } = [];

    /// <summary>No two live agents in a team with an equivalent role and a similar goal.</summary>
    [Id(6)] public bool PreventDuplicateRoles { get; init; } = true;

    /// <summary>How alike two goals must be (word overlap, 0–1) to count as the same work.</summary>
    [Id(7)] public double DuplicateGoalSimilarity { get; init; } = 0.75;

    /// <summary>Whether finished agents count towards team size and fan-out. True suits a task (its
    /// whole team over its life); false suits a long-lived workspace, where only live agents count.</summary>
    [Id(8)] public bool CountFinishedAgents { get; init; } = true;

    public bool IsEmpty => MaxAgents is null && GoalTypes.Count == 0 && SpawnerRoles.Count == 0 &&
                           SpawnerCapabilities.Count == 0 && MaxFanOutByDepth.Count == 0 && !PreventDuplicateRoles;
}

/// <summary>Why a spawn was refused, in a form the agent and the dashboard can act on.</summary>
public static class TeamRules
{
    public const string MaxAgents = "max_agents";
    public const string MaxAgentsForGoalType = "max_agents_for_goal_type";
    public const string SpawnerNotAllowed = "spawner_not_allowed";
    public const string FanOut = "max_fan_out";
    public const string DuplicateRole = "duplicate_role";
}

/// <summary>
/// Checks a spawn against team-shape policies. Pure and deterministic: the registry grain calls it
/// inside the same turn that registers the child, so concurrent spawns can't both slip under a limit.
/// </summary>
public static partial class TeamShapeValidator
{
    /// <param name="policies">Every policy that applies; all of them must allow the spawn.</param>
    /// <param name="parent">The agent asking to spawn.</param>
    /// <param name="team">Every agent of the parent's team (same root), the root included.</param>
    /// <param name="rootGoal">The task's goal, used to work out its goal type.</param>
    public static SpawnValidationResult Validate(
        IReadOnlyList<TeamPolicy> policies,
        AgentDirectoryEntry parent,
        IReadOnlyCollection<AgentDirectoryEntry> team,
        string childRole,
        string childGoal,
        string rootGoal)
    {
        foreach (var policy in policies)
        {
            if (Check(policy, policies, parent, team, childRole, childGoal, rootGoal) is { } rejection) return rejection;
        }

        return SpawnValidationResult.Allow(parent.Depth + 1);
    }

    private static SpawnValidationResult? Check(TeamPolicy policy, IReadOnlyList<TeamPolicy> all, AgentDirectoryEntry parent,
        IReadOnlyCollection<AgentDirectoryEntry> wholeTeam, string childRole, string childGoal, string rootGoal)
    {
        var team = policy.CountFinishedAgents ? wholeTeam : wholeTeam.Where(a => IsLive(a.Status)).ToList();
        if (policy.MaxAgents is { } max && team.Count >= max)
        {
            return Reject(TeamRules.MaxAgents,
                $"This team already has {team.Count} agents, the most its policy allows ({max}). Reuse an agent you have " +
                "(find_agents, send_message) or do the work yourself.",
                new() { ["limit"] = max, ["current"] = team.Count });
        }

        // A goal type stated on any policy (the task's) classifies the task for all of them.
        var goalType = all.Select(p => p.GoalType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? Classify(policy, rootGoal);
        if (goalType is not null &&
            policy.GoalTypes.FirstOrDefault(g => g.GoalType.Equals(goalType, StringComparison.OrdinalIgnoreCase)) is { } typeLimit &&
            team.Count >= typeLimit.MaxAgents)
        {
            return Reject(TeamRules.MaxAgentsForGoalType,
                $"'{typeLimit.GoalType}' tasks may use at most {typeLimit.MaxAgents} agents and this team has {team.Count}. " +
                "Reuse an agent you have or do the work yourself.",
                new() { ["goal_type"] = typeLimit.GoalType, ["limit"] = typeLimit.MaxAgents, ["current"] = team.Count });
        }

        if ((policy.SpawnerRoles.Count > 0 || policy.SpawnerCapabilities.Count > 0) &&
            !policy.SpawnerRoles.Any(r => PolicyEngine.Glob(r, parent.Role)) &&
            !policy.SpawnerCapabilities.Any(c => parent.Capabilities.Any(own => PolicyEngine.Glob(c, own))))
        {
            return Reject(TeamRules.SpawnerNotAllowed,
                $"Agents with the role '{parent.Role}' may not start other agents in this team. Do the work yourself, or " +
                "ask an agent that may spawn (send_message) to arrange it.",
                new() { ["role"] = parent.Role });
        }

        if (policy.MaxFanOutByDepth.Count > 0)
        {
            var limit = policy.MaxFanOutByDepth[Math.Min(parent.Depth, policy.MaxFanOutByDepth.Count - 1)];
            var children = team.Count(a => a.ParentAgentId == parent.AgentId);
            if (children >= limit)
            {
                return Reject(TeamRules.FanOut,
                    limit == 0
                        ? $"Agents at depth {parent.Depth} may not start agents in this team. Do the work yourself."
                        : $"Agents at depth {parent.Depth} may start at most {limit} agents and you have {children}. Reuse one, or do the work yourself.",
                    new() { ["depth"] = parent.Depth, ["limit"] = limit, ["current"] = children });
            }
        }

        if (policy.PreventDuplicateRoles &&
            wholeTeam.FirstOrDefault(a => IsLive(a.Status) && SameRole(a.Role, childRole) &&
                                     GoalSimilarity(a.Goal, childGoal) >= policy.DuplicateGoalSimilarity) is { } twin)
        {
            return Reject(TeamRules.DuplicateRole,
                $"'{twin.Role}' ({twin.AgentId}, {twin.Status}) is already working on this. Send it a message " +
                "(send_message) instead of starting a duplicate.",
                new() { ["existing_agent_id"] = twin.AgentId, ["existing_role"] = twin.Role }, twin.AgentId);
        }

        return null;
    }

    /// <summary>The first goal type whose keywords appear as whole words in the goal.</summary>
    public static string? Classify(TeamPolicy policy, string goal)
    {
        var words = Words(goal);
        return policy.GoalTypes.FirstOrDefault(g => g.Keywords.Any(k => Words(k).All(words.Contains)))?.GoalType;
    }

    /// <summary>"Database Specialist", "database-specialist agent" and "Database specialists" are one role.</summary>
    public static bool SameRole(string a, string b)
    {
        var x = NormalizeRole(a);
        var y = NormalizeRole(b);
        return x.Length > 0 && x == y;
    }

    public static string NormalizeRole(string role) =>
        string.Join(' ', NonWord().Split(role.ToLowerInvariant())
            .Where(w => w.Length > 0 && !RoleFiller.Contains(w))
            .Select(RoleStem));

    /// <summary>Jaccard overlap of the goals' meaningful words: 1 is identical, 0 nothing shared.</summary>
    public static double GoalSimilarity(string a, string b)
    {
        var x = Words(a).Where(w => !StopWords.Contains(w)).Select(Singular).ToHashSet();
        var y = Words(b).Where(w => !StopWords.Contains(w)).Select(Singular).ToHashSet();
        if (x.Count == 0 && y.Count == 0) return 1;
        var union = x.Union(y).Count();
        return union == 0 ? 0 : (double)x.Intersect(y).Count() / union;
    }

    private static HashSet<string> Words(string text) =>
        NonWord().Split(text.ToLowerInvariant()).Where(w => w.Length > 0).ToHashSet();

    /// <summary>"researcher" and "research", "developers" and "develop" name the same specialism.</summary>
    private static string RoleStem(string w)
    {
        w = Singular(w);
        return w.Length > 5 && w.EndsWith("er") ? w[..^2] : w;
    }

    private static string Singular(string w) => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w;

    private static bool IsLive(AgentStatus s) => s is not (AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Terminated or AgentStatus.TimedOut);

    private static SpawnValidationResult Reject(string rule, string message, Dictionary<string, object> details, string? existingAgentId = null) =>
        SpawnValidationResult.Reject(message) with
        {
            Rule = rule,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(details.Append(new("rule", rule)).ToDictionary()),
            ExistingAgentId = existingAgentId
        };

    private static readonly HashSet<string> RoleFiller = ["agent", "agents", "specialist", "specialists", "expert", "experts", "the", "a", "an", "lead", "senior"];

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "and", "or", "of", "for", "to", "in", "on", "with", "by", "at", "from", "as", "is", "are", "be", "this", "that",
        "these", "those", "it", "its", "our", "your", "their", "into", "about", "all", "any", "each", "using", "use", "based", "will", "should"
    ];

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
