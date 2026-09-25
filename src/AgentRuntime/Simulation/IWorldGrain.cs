using Orleans.Concurrency;

namespace AgentRuntime.Simulation;

/// <summary>
/// The shared environment of a simulation (CLAUDE.md section 27): locations, a public board, an
/// energy economy and removal votes, plus a world clock that wakes residents each tick. The world
/// grain owns all of this state; residents change it only by calling <see cref="Act"/> through
/// their tools, and the grain validates and charges every action (section 50: the LLM decides,
/// the runtime executes).
///
/// Deadlock rule: every call this grain makes into a resident is either interleaved or one-way,
/// because residents call <see cref="Act"/> from inside their own reasoning turns — if the world
/// awaited a non-interleaved resident method while that resident awaited the world, both would
/// hang until Orleans' response timeout.
/// </summary>
public interface IWorldGrain : IGrainWithStringKey
{
    /// <summary>Creates the world and its initial residents from a genesis blueprint.</summary>
    Task Create(WorldBlueprint blueprint, WorldSettings settings);

    /// <summary>Starts the world clock. The first tick fires immediately.</summary>
    Task Start();

    Task Pause();

    Task Resume();

    /// <summary>Ends the world: stops the clock and retires every resident.</summary>
    Task End(string reason);

    /// <summary>
    /// A resident's action, called from world tools during its reasoning turn. The
    /// <paramref name="idempotencyKey"/> is the tool call's key: a replay with the same key returns
    /// the recorded result without acting again.
    /// </summary>
    Task<WorldActionResult> Act(string agentId, WorldAction action, string idempotencyKey);

    /// <summary>Read-only; interleaves so the dashboard can poll mid-tick. Null if this world
    /// doesn't exist in memory (never created, or lost in a restart).</summary>
    [AlwaysInterleave]
    Task<WorldSnapshot?> GetSnapshot();
}
