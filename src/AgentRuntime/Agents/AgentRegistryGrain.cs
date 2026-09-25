using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace AgentRuntime.Agents;

[GenerateSerializer]
public sealed class RegistryState
{
    [Id(0)] public Dictionary<string, AgentDirectoryEntry> Agents { get; set; } = [];
}

public sealed class AgentRegistryGrain(
    [PersistentState("registry", "Default")] IPersistentState<RegistryState> state,
    IOptions<RuntimeLimitsOptions> limitsOptions,
    ILogger<AgentRegistryGrain> logger) : Grain, IAgentRegistryGrain
{
    private readonly RuntimeLimitsOptions _limits = limitsOptions.Value;

    public Task<SpawnValidationResult> ValidateSpawnAsync(string? parentAgentId, string? role = null)
    {
        var totalAgents = state.State.Agents.Count;
        var activeAgents = state.State.Agents.Values.Count(a => IsActive(a.Status));

        if (totalAgents >= _limits.MaxTotalAgents)
        {
            return Task.FromResult(SpawnValidationResult.Reject(
                $"Total agent limit reached ({totalAgents}/{_limits.MaxTotalAgents})."));
        }

        if (activeAgents >= _limits.MaxActiveAgents)
        {
            return Task.FromResult(SpawnValidationResult.Reject(
                $"Active agent limit reached ({activeAgents}/{_limits.MaxActiveAgents})."));
        }

        var depth = 0;
        if (parentAgentId is not null)
        {
            if (!state.State.Agents.TryGetValue(parentAgentId, out var parent))
            {
                return Task.FromResult(SpawnValidationResult.Reject($"Unknown parent agent '{parentAgentId}'."));
            }

            depth = parent.Depth + 1;
            if (depth > _limits.MaxAgentDepth)
            {
                return Task.FromResult(SpawnValidationResult.Reject(
                    $"Max agent depth exceeded ({depth}/{_limits.MaxAgentDepth})."));
            }

            var childCount = state.State.Agents.Values.Count(a => a.ParentAgentId == parentAgentId);
            if (childCount >= _limits.MaxChildrenPerAgent)
            {
                return Task.FromResult(SpawnValidationResult.Reject(
                    $"Max children per agent exceeded ({childCount}/{_limits.MaxChildrenPerAgent})."));
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                var duplicate = state.State.Agents.Values.FirstOrDefault(a =>
                    a.ParentAgentId == parentAgentId && string.Equals(a.Role, role, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null)
                {
                    return Task.FromResult(SpawnValidationResult.Reject(
                        $"You already have a '{role}' agent ({duplicate.AgentId}, status={duplicate.Status}). " +
                        "Use find_agents or send_message to collaborate with it instead of spawning a duplicate."));
                }
            }
        }

        return Task.FromResult(SpawnValidationResult.Allow(depth));
    }

    public async Task RegisterAsync(AgentDirectoryEntry entry)
    {
        state.State.Agents[entry.AgentId] = entry;
        await state.WriteStateAsync();
        logger.LogInformation("Registered agent {AgentId} (parent={ParentAgentId}, depth={Depth})",
            entry.AgentId, entry.ParentAgentId, entry.Depth);
    }

    public async Task<SpawnValidationResult> TryRegisterSpawnAsync(AgentDirectoryEntry entry, string? role = null)
    {
        // Idempotent for replays: re-registering the same id (a spawn retried after a crash, with
        // an id derived from its idempotency key) succeeds without counting against any limit.
        if (state.State.Agents.TryGetValue(entry.AgentId, out var existing) && existing.ParentAgentId == entry.ParentAgentId)
        {
            return SpawnValidationResult.Allow(existing.Depth);
        }

        var validation = await ValidateSpawnAsync(entry.ParentAgentId, role);
        if (!validation.Allowed)
        {
            return validation;
        }

        await RegisterAsync(entry with { Depth = validation.AllowedDepth });
        return validation;
    }

    public async Task UpdateStatusAsync(string agentId, AgentStatus status)
    {
        if (state.State.Agents.TryGetValue(agentId, out var entry))
        {
            state.State.Agents[agentId] = entry with { Status = status };
            await state.WriteStateAsync();
        }
    }

    public async Task UnregisterAsync(string agentId)
    {
        if (state.State.Agents.Remove(agentId))
        {
            await state.WriteStateAsync();
        }
    }

    public Task<IReadOnlyList<AgentDirectoryEntry>> FindAsync(FindAgentsQuery query)
    {
        IEnumerable<AgentDirectoryEntry> results = state.State.Agents.Values;

        if (query.Status is { } status)
        {
            results = results.Where(a => a.Status == status);
        }

        if (query.RootAgentId is { } rootId)
        {
            results = results.Where(a => a.RootAgentId == rootId);
        }

        if (query.Capabilities is { Count: > 0 } caps)
        {
            results = results.Where(a => caps.All(c => a.Capabilities.Contains(c, StringComparer.OrdinalIgnoreCase)));
        }

        return Task.FromResult<IReadOnlyList<AgentDirectoryEntry>>(results.ToList());
    }

    public Task<AgentDirectoryEntry?> GetAsync(string agentId) =>
        Task.FromResult(state.State.Agents.GetValueOrDefault(agentId));

    public Task<IReadOnlyList<string>> GetChildrenAsync(string agentId) =>
        Task.FromResult<IReadOnlyList<string>>(
            state.State.Agents.Values.Where(a => a.ParentAgentId == agentId).Select(a => a.AgentId).ToList());

    public Task<IReadOnlyList<AgentDirectoryEntry>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<AgentDirectoryEntry>>(state.State.Agents.Values.ToList());

    public async Task ClearAsync()
    {
        state.State.Agents.Clear();
        await state.WriteStateAsync();
        logger.LogInformation("Agent registry cleared by admin reset.");
    }

    private static bool IsActive(AgentStatus status) => status is
        AgentStatus.Created or AgentStatus.Initializing or AgentStatus.Idle or AgentStatus.Thinking or
        AgentStatus.Executing or AgentStatus.Waiting or AgentStatus.Spawning;
}
