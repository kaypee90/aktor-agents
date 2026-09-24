using AgentRuntime.Contracts;
using AgentRuntime.Messaging;
using AgentRuntime.Simulation;

namespace AgentRuntime.Agents;

/// <summary>
/// The authoritative "Agent Runtime" described in CLAUDE.md section 8/50: creates agents,
/// validates spawn requests against global limits, routes messages, and enforces permissions.
/// The LLM decides what it wants; every method here is where that decision gets checked and
/// (if valid) turned into an actual side effect. Named Orchestrator rather than "AgentRuntime" to
/// avoid colliding with this project's own namespace.
/// </summary>
public interface IAgentOrchestrator
{
    Task<string> CreateRootAgentAsync(string taskId, string goal, ResourceBudget? budget = null,
        CancellationToken cancellationToken = default);

    Task<SpawnAgentResult> SpawnAgentAsync(string parentAgentId, SpawnAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentMessageAck> SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentDirectoryEntry>> FindAgentsAsync(FindAgentsQuery query,
        CancellationToken cancellationToken = default);

    Task<AgentSnapshot?> GetSnapshotAsync(string agentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListChildrenAsync(string agentId, CancellationToken cancellationToken = default);

    Task PauseAsync(string agentId, CancellationToken cancellationToken = default);

    Task ResumeAsync(string agentId, CancellationToken cancellationToken = default);

    Task StopAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Creates a simulation resident: registered under the world (RootAgentId = world id)
    /// with world tools only and a per-resident safety budget. Returns a rejection if the registry's
    /// global limits (total/active agents, depth, children) refuse it.</summary>
    Task<SpawnAgentResult> CreateResidentAsync(ResidentCreationRequest request, CancellationToken cancellationToken = default);

    Task RetireAsync(string agentId, string reason, CancellationToken cancellationToken = default);

    Task UnpauseAsync(string agentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentDirectoryEntry>> GetAllAgentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Wipes the live agent registry. Durable history in Postgres is a separate concern —
    /// callers doing a full reset (e.g. the admin reset endpoint) clear both.</summary>
    Task ResetRegistryAsync(CancellationToken cancellationToken = default);
}
