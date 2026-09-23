using System.Collections.Concurrent;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Messaging;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;
using Orleans;

namespace AgentRuntime.Agents;

public sealed class AgentOrchestrator(
    IGrainFactory grainFactory,
    ToolRegistry toolRegistry,
    IEventPublisher events,
    IOptions<RuntimeLimitsOptions> limitsOptions,
    IOptions<DefaultBudgetOptions> defaultBudgetOptions) : IAgentOrchestrator
{
    private readonly RuntimeLimitsOptions _limits = limitsOptions.Value;
    private readonly DefaultBudgetOptions _defaultBudget = defaultBudgetOptions.Value;

    // In-process guard against runaway message storms within a single task (CLAUDE.md section 23).
    // A production deployment would back this with the registry grain / Postgres instead.
    private readonly ConcurrentDictionary<string, int> _messageCountsByTask = new();

    private IAgentRegistryGrain Registry => grainFactory.GetGrain<IAgentRegistryGrain>(0);

    public async Task<string> CreateRootAgentAsync(
        string taskId, string goal, ResourceBudget? budget = null, CancellationToken cancellationToken = default)
    {
        var agentId = $"root-{Guid.NewGuid().ToString("n")[..8]}";
        var effectiveBudget = budget ?? _defaultBudget.ToBudget();
        // "filesystem" so the root can write a final consolidated report before completing
        // (CLAUDE.md section 26/52 — the task should produce an inspectable final artifact).
        var allowedTools = AgentToolCatalog.ResolveToolsForCapabilities(["research", "web-search", "filesystem"]);
        var permissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages | ToolPermission.NetworkAccess
                           | ToolPermission.ReadFilesystem | ToolPermission.WriteFilesystem;
        allowedTools = FilterToolsByPermission(allowedTools, permissions);

        var validation = await Registry.TryRegisterSpawnAsync(new AgentDirectoryEntry
        {
            AgentId = agentId,
            Role = "Root Agent",
            Goal = goal,
            Status = AgentStatus.Created,
            Capabilities = ["orchestration"],
            ParentAgentId = null,
            Depth = 0,
            RootAgentId = agentId
        });
        if (!validation.Allowed)
        {
            throw new InvalidOperationException($"Cannot create root agent: {validation.RejectionReason}");
        }

        var grain = grainFactory.GetGrain<IAgentGrain>(agentId);
        await grain.Initialize(new AgentInitializationRequest
        {
            AgentId = agentId,
            ParentAgentId = null,
            RootAgentId = agentId,
            Name = "Root",
            Role = "Root Agent",
            Goal = goal,
            Capabilities = ["orchestration"],
            AllowedTools = allowedTools.ToList(),
            GrantedPermissions = permissions,
            Budget = effectiveBudget,
            Depth = 0,
            TaskId = taskId
        });

        await events.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.TaskCreated,
            AgentId = agentId,
            TaskId = taskId,
            Summary = $"Task created: {goal}",
            Data = new Dictionary<string, string> { ["goal"] = goal, ["rootAgentId"] = agentId }
        }, cancellationToken);

        // One-way: returns as soon as the request is queued, not when the reasoning loop ends.
        await grain.Start();

        return agentId;
    }

    public async Task<SpawnAgentResult> SpawnAgentAsync(
        string parentAgentId, SpawnAgentRequest request, CancellationToken cancellationToken = default)
    {
        var parentEntry = await Registry.GetAsync(parentAgentId);
        if (parentEntry is null)
        {
            return await RejectSpawnAsync(parentAgentId, null, $"Unknown parent agent '{parentAgentId}'.", cancellationToken);
        }

        var parentGrain = grainFactory.GetGrain<IAgentGrain>(parentAgentId);
        var parentSnapshot = await parentGrain.GetSnapshot();

        // The per-agent MaxChildren budget (CLAUDE.md section 24) is separate from, and usually
        // tighter than, the global MaxChildrenPerAgent limit the registry enforces.
        if (parentSnapshot.Usage.ChildrenSpawned >= parentSnapshot.Budget.MaxChildren)
        {
            return await RejectSpawnAsync(parentAgentId, parentSnapshot.TaskId,
                $"Child-agent budget exhausted ({parentSnapshot.Usage.ChildrenSpawned}/{parentSnapshot.Budget.MaxChildren}).",
                cancellationToken);
        }

        // Elapsed time isn't tracked incrementally in ResourceUsage, so derive it here; otherwise a
        // child spawned late in the parent's life would get the parent's full original duration.
        var elapsedSeconds = parentSnapshot.StartedAt is { } startedAt
            ? (int)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalSeconds)
            : 0;
        var childBudget = parentSnapshot.Budget.DeriveChildBudget(
            parentSnapshot.Usage with { ElapsedSeconds = elapsedSeconds }, request.RequestedBudget);
        if (childBudget.IsEmpty || childBudget.MaxTokens < _limits.MinChildTokens || childBudget.MaxToolCalls < _limits.MinChildToolCalls)
        {
            return await RejectSpawnAsync(parentAgentId, parentSnapshot.TaskId,
                $"Not enough budget left to fund a useful new agent: it would get {childBudget.MaxTokens} tokens and " +
                $"{childBudget.MaxToolCalls} tool calls (minimum {_limits.MinChildTokens} / {_limits.MinChildToolCalls}). " +
                "Do this work yourself, or call complete_task.", cancellationToken);
        }

        var requestedTools = AgentToolCatalog.ResolveToolsForCapabilities(
            request.Capabilities.Concat(request.RequestedTools ?? []));
        // A child can never see a tool the parent itself couldn't see, nor exercise a permission
        // the parent lacks — capability-based resolution is clamped by the parent's own grant
        // (CLAUDE.md section 44/51). Governance tools (spawn/message/etc.) are always available.
        var inheritedWorkspaceTools = parentSnapshot.AllowedTools.Intersect(AgentToolCatalog.WorkspaceTools, StringComparer.OrdinalIgnoreCase);
        var childTools = FilterToolsByPermission(
            requestedTools.Intersect(parentSnapshot.AllowedTools.Concat(AgentToolCatalog.GovernanceTools), StringComparer.OrdinalIgnoreCase)
                .Union(inheritedWorkspaceTools, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            parentSnapshot.GrantedPermissions);
        var childPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages | InferPermissionsForTools(childTools);

        var childId = $"agent-{Guid.NewGuid().ToString("n")[..8]}";

        // Validate and register atomically in one registry turn so concurrent spawns from
        // different parents can't both pass the total/active-agent limit checks.
        var validation = await Registry.TryRegisterSpawnAsync(new AgentDirectoryEntry
        {
            AgentId = childId,
            Role = request.Role,
            Goal = request.Goal,
            Status = AgentStatus.Created,
            Capabilities = request.Capabilities,
            ParentAgentId = parentAgentId,
            Depth = parentEntry.Depth + 1,
            RootAgentId = parentSnapshot.RootAgentId
        }, request.Role);
        if (!validation.Allowed)
        {
            return await RejectSpawnAsync(parentAgentId, parentSnapshot.TaskId, validation.RejectionReason ?? "Spawn rejected.", cancellationToken);
        }

        await events.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.AgentSpawnRequested,
            AgentId = parentAgentId,
            TargetAgentId = childId,
            TaskId = parentSnapshot.TaskId,
            Summary = $"Agent '{parentAgentId}' requested a new '{request.Role}' agent."
        }, cancellationToken);

        var childGrain = grainFactory.GetGrain<IAgentGrain>(childId);
        await childGrain.Initialize(new AgentInitializationRequest
        {
            AgentId = childId,
            ParentAgentId = parentAgentId,
            RootAgentId = parentSnapshot.RootAgentId,
            Name = request.Role,
            Role = request.Role,
            Goal = request.Goal,
            Capabilities = request.Capabilities,
            AllowedTools = childTools,
            GrantedPermissions = childPermissions,
            Budget = childBudget,
            Depth = validation.AllowedDepth,
            InitialContext = request.InitialContext,
            TaskId = parentSnapshot.TaskId
        });

        await events.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.AgentSpawned,
            AgentId = parentAgentId,
            TargetAgentId = childId,
            TaskId = parentSnapshot.TaskId,
            Summary = $"Spawned '{request.Role}' agent {childId}."
        }, cancellationToken);

        await childGrain.Start();

        return new SpawnAgentResult { AgentId = childId, Status = "created", GrantedBudget = childBudget };
    }

    public async Task<AgentMessageAck> SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        if (string.Equals(message.FromAgentId, message.ToAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentMessageAck { MessageId = message.MessageId, Accepted = false, RejectionReason = "An agent cannot message itself." };
        }

        var count = _messageCountsByTask.AddOrUpdate(message.TaskId, 1, (_, c) => c + 1);
        if (count > _limits.MaxMessagesPerTask)
        {
            return new AgentMessageAck
            {
                MessageId = message.MessageId,
                Accepted = false,
                RejectionReason = $"Task '{message.TaskId}' exceeded its max message count ({_limits.MaxMessagesPerTask})."
            };
        }

        // GetGrain would otherwise silently activate an empty agent for a typo'd/hallucinated id
        // and report the message as delivered.
        if (await Registry.GetAsync(message.ToAgentId) is null)
        {
            return new AgentMessageAck { MessageId = message.MessageId, Accepted = false, RejectionReason = $"No such agent '{message.ToAgentId}'." };
        }

        var target = grainFactory.GetGrain<IAgentGrain>(message.ToAgentId);

        await events.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.AgentMessageSent,
            AgentId = message.FromAgentId,
            TargetAgentId = message.ToAgentId,
            TaskId = message.TaskId,
            CorrelationId = message.CorrelationId,
            Summary = $"{message.FromAgentId} -> {message.ToAgentId}: {message.MessageType}",
            Data = new Dictionary<string, string>
            {
                ["messageId"] = message.MessageId,
                ["conversationId"] = message.ConversationId,
                ["messageType"] = message.MessageType.ToString(),
                ["payload"] = message.Payload
            }
        }, cancellationToken);

        return await target.SendMessage(message);
    }

    public Task<IReadOnlyList<AgentDirectoryEntry>> FindAgentsAsync(FindAgentsQuery query, CancellationToken cancellationToken = default) =>
        Registry.FindAsync(query);

    public async Task<AgentSnapshot?> GetSnapshotAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var entry = await Registry.GetAsync(agentId);
        if (entry is null) return null;
        return await grainFactory.GetGrain<IAgentGrain>(agentId).GetSnapshot();
    }

    public Task<IReadOnlyList<string>> ListChildrenAsync(string agentId, CancellationToken cancellationToken = default) =>
        Registry.GetChildrenAsync(agentId);

    public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) =>
        grainFactory.GetGrain<IAgentGrain>(agentId).Pause();

    public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) =>
        grainFactory.GetGrain<IAgentGrain>(agentId).Resume();

    public Task StopAsync(string agentId, CancellationToken cancellationToken = default) =>
        grainFactory.GetGrain<IAgentGrain>(agentId).Stop();

    public Task<IReadOnlyList<AgentDirectoryEntry>> GetAllAgentsAsync(CancellationToken cancellationToken = default) =>
        Registry.GetAllAsync();

    public Task ResetRegistryAsync(CancellationToken cancellationToken = default)
    {
        _messageCountsByTask.Clear();
        return Registry.ClearAsync();
    }

    private async Task<SpawnAgentResult> RejectSpawnAsync(
        string parentAgentId, string? taskId, string reason, CancellationToken cancellationToken)
    {
        await events.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.AgentSpawnRequested,
            AgentId = parentAgentId,
            TaskId = taskId,
            Summary = $"Spawn rejected: {reason}"
        }, cancellationToken);

        return new SpawnAgentResult { AgentId = string.Empty, Status = "rejected", RejectionReason = reason };
    }

    private List<string> FilterToolsByPermission(IReadOnlyList<string> toolNames, ToolPermission allowed)
    {
        return toolNames.Where(name =>
        {
            if (!toolRegistry.TryGet(name, out var tool)) return AgentToolCatalog.GovernanceTools.Contains(name, StringComparer.OrdinalIgnoreCase);
            return (tool.Definition.RequiredPermissions & ~allowed) == ToolPermission.None;
        }).ToList();
    }

    private ToolPermission InferPermissionsForTools(IReadOnlyList<string> toolNames)
    {
        var permissions = ToolPermission.None;
        foreach (var name in toolNames)
        {
            if (toolRegistry.TryGet(name, out var tool))
            {
                permissions |= tool.Definition.RequiredPermissions;
            }
        }

        return permissions;
    }
}
