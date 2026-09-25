using AgentRuntime.Contracts;

namespace AgentRuntime.Agents;

/// <summary>
/// Singleton actor (single activation, key 0) that is the authoritative source for global spawn
/// limits and agent discovery (CLAUDE.md sections 8, 10, 11, 47). Being an ordinary grain gives it
/// Orleans' single-threaded turn execution, so concurrent spawn validation never races.
/// </summary>
public interface IAgentRegistryGrain : IGrainWithIntegerKey
{
    /// <summary>Validates depth/children/total/active limits, and — when <paramref name="role"/>
    /// is supplied — rejects spawning a sibling with the same role as one the parent already has.
    /// This is a runtime-enforced anti-duplication guard (CLAUDE.md section 11: "prefer
    /// collaborating with an existing suitable agent"), not something left to the LLM's prompt
    /// discipline: a model that reacts to a tool failure by spawning another copy of itself would
    /// otherwise fan out unboundedly, burning through both the token budget and, with a real
    /// provider, its rate limit.</summary>
    /// <remarks>Total and active agent limits count the agent's own organization only, and
    /// <paramref name="tenantMaxActive"/> (its plan's limit, 0 = none) applies on top.</remarks>
    Task<SpawnValidationResult> ValidateSpawnAsync(string? parentAgentId, string? role = null, string? tenantId = null, int tenantMaxActive = 0);

    Task RegisterAsync(AgentDirectoryEntry entry);

    /// <summary>Validates (as <see cref="ValidateSpawnAsync"/>) and, if allowed, registers the entry
    /// in the same grain turn with its depth set to the validated depth. Validating and registering
    /// in separate calls lets two concurrent spawns both pass the total/active limit checks.</summary>
    Task<SpawnValidationResult> TryRegisterSpawnAsync(AgentDirectoryEntry entry, string? role = null, int tenantMaxActive = 0);

    Task UpdateStatusAsync(string agentId, AgentStatus status);

    Task UnregisterAsync(string agentId);

    Task<IReadOnlyList<AgentDirectoryEntry>> FindAsync(FindAgentsQuery query);

    Task<AgentDirectoryEntry?> GetAsync(string agentId);

    Task<IReadOnlyList<string>> GetChildrenAsync(string agentId);

    Task<IReadOnlyList<AgentDirectoryEntry>> GetAllAsync();

    /// <summary>Wipes the registry (admin reset only — see CLAUDE.md section 32's human controls).
    /// Existing agent grain activations are left to idle out naturally; nothing new can reference
    /// their old ids once the registry and durable history are both cleared.</summary>
    Task ClearAsync();
}
