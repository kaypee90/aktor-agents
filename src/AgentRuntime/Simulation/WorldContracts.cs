namespace AgentRuntime.Simulation;

public enum WorldStatus
{
    Created,
    Running,
    Paused,
    Ended
}

/// <summary>A resident's standing in the world — separate from its agent lifecycle status.</summary>
public enum ResidentState
{
    Active,
    /// <summary>Out of energy: the agent is paused and wakes only if someone gives it energy.</summary>
    Dormant,
    /// <summary>Removed by a passed vote.</summary>
    Removed,
    /// <summary>Chose to leave the world.</summary>
    Left
}

public enum ProposalOutcome
{
    Open,
    Passed,
    Rejected
}

public static class WorldIds
{
    public const string Prefix = "world-";

    public static string New() => Prefix + Guid.NewGuid().ToString("n")[..8];

    /// <summary>Residents use their world id as their task id, so this identifies simulation traffic.</summary>
    public static bool IsWorld(string? taskId) => taskId?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}

// ---- Blueprint (output of genesis, input to world creation) ----------------

[GenerateSerializer]
public sealed record LocationBlueprint
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public string Description { get; init; } = string.Empty;
}

[GenerateSerializer]
public sealed record ResidentBlueprint
{
    [Id(0)] public required string Name { get; init; }
    /// <summary>Short occupation/identity, e.g. "baker". Shown as the agent's role.</summary>
    [Id(1)] public required string Role { get; init; }
    [Id(2)] public string Persona { get; init; } = string.Empty;
    /// <summary>What this resident wants out of life in the world — becomes the agent's goal.</summary>
    [Id(3)] public string Drives { get; init; } = string.Empty;
    [Id(4)] public string? StartingLocation { get; init; }
    [Id(5)] public string? Relationships { get; init; }
}

[GenerateSerializer]
public sealed record WorldBlueprint
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public string Description { get; init; } = string.Empty;
    [Id(2)] public List<LocationBlueprint> Locations { get; init; } = [];
    [Id(3)] public List<ResidentBlueprint> Residents { get; init; } = [];
}

[GenerateSerializer]
public sealed record WorldSettings
{
    [Id(0)] public required string Seed { get; init; }
    [Id(1)] public int TickIntervalSeconds { get; init; }
    [Id(2)] public int MaxTicks { get; init; }
    [Id(3)] public int MaxDurationMinutes { get; init; }
}

// ---- Live world state ------------------------------------------------------

[GenerateSerializer]
public sealed class ResidentRecord
{
    [Id(0)] public required string AgentId { get; set; }
    [Id(1)] public required string Name { get; set; }
    [Id(2)] public required string Role { get; set; }
    [Id(3)] public string Persona { get; set; } = string.Empty;
    [Id(4)] public string Drives { get; set; } = string.Empty;
    [Id(5)] public required string Location { get; set; }
    [Id(6)] public int Energy { get; set; }
    [Id(7)] public ResidentState State { get; set; } = ResidentState.Active;
    [Id(8)] public string? ParentAgentId { get; set; }
    [Id(9)] public int JoinedTick { get; set; }
    [Id(10)] public List<string> Notes { get; set; } = [];
    [Id(11)] public string? LastPlan { get; set; }
    /// <summary>Highest activity sequence number already delivered to this resident as perception.</summary>
    [Id(12)] public long LastSeenSeq { get; set; }
    [Id(13)] public int? LeftTick { get; set; }
}

[GenerateSerializer]
public sealed record BoardPost
{
    [Id(0)] public required int Tick { get; init; }
    [Id(1)] public required string AuthorId { get; init; }
    [Id(2)] public required string AuthorName { get; init; }
    [Id(3)] public required string Text { get; init; }
    [Id(4)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed class RemovalProposal
{
    [Id(0)] public required string ProposalId { get; set; }
    [Id(1)] public required string TargetId { get; set; }
    [Id(2)] public required string ProposerId { get; set; }
    [Id(3)] public string Reason { get; set; } = string.Empty;
    [Id(4)] public int OpenedTick { get; set; }
    [Id(5)] public int DeadlineTick { get; set; }
    /// <summary>Fixed when the proposal opens, so arrivals/departures can't swing a vote in progress.</summary>
    [Id(6)] public List<string> EligibleVoters { get; set; } = [];
    [Id(7)] public Dictionary<string, bool> Votes { get; set; } = [];
    [Id(8)] public ProposalOutcome Outcome { get; set; } = ProposalOutcome.Open;
}

/// <summary>One thing that happened in the world. Residents perceive entries at their location
/// (or global ones); the dashboard shows all of them, including private plans.</summary>
[GenerateSerializer]
public sealed record WorldActivityEntry
{
    [Id(0)] public long Seq { get; init; }
    [Id(1)] public int Tick { get; init; }
    /// <summary>said, talked, moved, posted, gave, proposed, voted, removed, rejected, dormant,
    /// revived, joined, left, plan, note, world.</summary>
    [Id(2)] public required string Kind { get; init; }
    [Id(3)] public string? ActorId { get; init; }
    [Id(4)] public string? TargetId { get; init; }
    [Id(5)] public string? Location { get; init; }
    [Id(6)] public string? FromLocation { get; init; }
    [Id(7)] public string Text { get; init; } = string.Empty;
    [Id(8)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Visible everywhere, not only at <see cref="Location"/>.</summary>
    [Id(9)] public bool Global { get; init; }
    /// <summary>Never shown to other residents (direct messages, plans, notes) — dashboard only.</summary>
    [Id(10)] public bool Private { get; init; }
    /// <summary>The exact words, for speech/posts/plans — lets the dashboard render chat bubbles.</summary>
    [Id(11)] public string? Quote { get; init; }
}

[GenerateSerializer]
public sealed class WorldState
{
    [Id(0)] public string WorldId { get; set; } = string.Empty;
    [Id(1)] public string Name { get; set; } = string.Empty;
    [Id(2)] public string Description { get; set; } = string.Empty;
    [Id(3)] public string Seed { get; set; } = string.Empty;
    [Id(4)] public WorldStatus Status { get; set; } = WorldStatus.Created;
    [Id(5)] public int Tick { get; set; }
    [Id(6)] public int MaxTicks { get; set; }
    [Id(7)] public int TickIntervalSeconds { get; set; }
    [Id(8)] public int MaxDurationMinutes { get; set; }
    [Id(9)] public DateTimeOffset CreatedAt { get; set; }
    [Id(10)] public DateTimeOffset? StartedAt { get; set; }
    [Id(11)] public DateTimeOffset? EndsAt { get; set; }
    [Id(12)] public DateTimeOffset? EndedAt { get; set; }
    [Id(13)] public string? EndReason { get; set; }
    [Id(14)] public List<LocationBlueprint> Locations { get; set; } = [];
    [Id(15)] public Dictionary<string, ResidentRecord> Residents { get; set; } = [];
    [Id(16)] public List<BoardPost> Board { get; set; } = [];
    [Id(17)] public Dictionary<string, RemovalProposal> Proposals { get; set; } = [];
    [Id(18)] public List<WorldActivityEntry> Activity { get; set; } = [];
    [Id(19)] public long NextSeq { get; set; } = 1;
    /// <summary>Time already spent running before the current pause, so pausing doesn't eat the clock.</summary>
    [Id(20)] public double ElapsedSecondsBeforePause { get; set; }
    [Id(21)] public DateTimeOffset? RunningSince { get; set; }
    [Id(22)] public int NextProposalNumber { get; set; } = 1;
    /// <summary>Recent action results by idempotency key (bounded), for replay deduplication.</summary>
    [Id(23)] public Dictionary<string, WorldActionResult> ActionResults { get; set; } = [];
    [Id(24)] public List<string> ActionResultOrder { get; set; } = [];
}

// ---- Actions ----------------------------------------------------------------

[GenerateSerializer]
public sealed record WorldAction
{
    /// <summary>look, move, say, talk, post, give, propose_removal, vote, bring, note, leave, end_turn.</summary>
    [Id(0)] public required string Kind { get; init; }
    [Id(1)] public string? Text { get; init; }
    [Id(2)] public string? TargetId { get; init; }
    [Id(3)] public string? Location { get; init; }
    [Id(4)] public int Amount { get; init; }
    [Id(5)] public string? ProposalId { get; init; }
    [Id(6)] public bool Support { get; init; }
    [Id(7)] public string? Name { get; init; }
    [Id(8)] public string? Role { get; init; }
    [Id(9)] public string? Persona { get; init; }
    [Id(10)] public string? Drives { get; init; }
}

[GenerateSerializer]
public sealed record WorldActionResult
{
    [Id(0)] public bool Success { get; init; }
    [Id(1)] public string Message { get; init; } = string.Empty;
    /// <summary>Tool-facing JSON payload on success.</summary>
    [Id(2)] public string? ResultJson { get; init; }

    public static WorldActionResult Ok(string message, string? json = null) => new() { Success = true, Message = message, ResultJson = json };
    public static WorldActionResult Fail(string message) => new() { Success = false, Message = message };
}

// ---- Snapshot (API/dashboard view) -------------------------------------------

[GenerateSerializer]
public sealed record ResidentView
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public required string Role { get; init; }
    [Id(3)] public string Persona { get; init; } = string.Empty;
    [Id(4)] public string Drives { get; init; } = string.Empty;
    [Id(5)] public required string Location { get; init; }
    [Id(6)] public int Energy { get; init; }
    [Id(7)] public ResidentState State { get; init; }
    [Id(8)] public string? AgentStatus { get; init; }
    [Id(9)] public string? ParentAgentId { get; init; }
    [Id(10)] public int JoinedTick { get; init; }
    [Id(11)] public List<string> Notes { get; init; } = [];
    [Id(12)] public string? LastPlan { get; init; }
    [Id(13)] public int TokensUsed { get; init; }
    [Id(14)] public decimal CostUsd { get; init; }
}

[GenerateSerializer]
public sealed record ProposalView
{
    [Id(0)] public required string ProposalId { get; init; }
    [Id(1)] public required string TargetId { get; init; }
    [Id(2)] public required string ProposerId { get; init; }
    [Id(3)] public string Reason { get; init; } = string.Empty;
    [Id(4)] public int OpenedTick { get; init; }
    [Id(5)] public int DeadlineTick { get; init; }
    [Id(6)] public int EligibleVoters { get; init; }
    [Id(7)] public Dictionary<string, bool> Votes { get; init; } = [];
    [Id(8)] public ProposalOutcome Outcome { get; init; }
}

[GenerateSerializer]
public sealed record WorldTotals
{
    [Id(0)] public int TokensUsed { get; init; }
    [Id(1)] public decimal CostUsd { get; init; }
    [Id(2)] public int ActiveResidents { get; init; }
    [Id(3)] public int TotalResidents { get; init; }
}

[GenerateSerializer]
public sealed record WorldSnapshot
{
    [Id(0)] public required string WorldId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public string Description { get; init; } = string.Empty;
    [Id(3)] public string Seed { get; init; } = string.Empty;
    [Id(4)] public WorldStatus Status { get; init; }
    [Id(5)] public int Tick { get; init; }
    [Id(6)] public int MaxTicks { get; init; }
    [Id(7)] public int TickIntervalSeconds { get; init; }
    [Id(8)] public DateTimeOffset CreatedAt { get; init; }
    [Id(9)] public DateTimeOffset? StartedAt { get; init; }
    [Id(10)] public DateTimeOffset? EndsAt { get; init; }
    [Id(11)] public DateTimeOffset? EndedAt { get; init; }
    [Id(12)] public string? EndReason { get; init; }
    [Id(13)] public List<LocationBlueprint> Locations { get; init; } = [];
    [Id(14)] public List<ResidentView> Residents { get; init; } = [];
    [Id(15)] public List<BoardPost> Board { get; init; } = [];
    [Id(16)] public List<ProposalView> Proposals { get; init; } = [];
    [Id(17)] public List<WorldActivityEntry> Activity { get; init; } = [];
    [Id(18)] public WorldTotals Totals { get; init; } = new();
    [Id(19)] public string EnergyCosts { get; init; } = string.Empty;
    [Id(20)] public int MaxEnergy { get; init; }
}

/// <summary>What the world asks the runtime for when a resident is born (genesis or bring_new_agent).</summary>
public sealed record ResidentCreationRequest
{
    public required string WorldId { get; init; }
    public required string WorldName { get; init; }
    public string WorldDescription { get; init; } = string.Empty;
    public required string Name { get; init; }
    public required string Role { get; init; }
    public string Persona { get; init; } = string.Empty;
    public string Drives { get; init; } = string.Empty;
    public string? Relationships { get; init; }
    public string? ParentAgentId { get; init; }
    public int MaxDurationMinutes { get; init; }
}

/// <summary>Durable copy of world snapshots (CLAUDE.md section 39: Postgres is the source of truth
/// for history; the world grain's own state is in-memory and lost on restart).</summary>
public interface IWorldArchive
{
    Task SaveAsync(WorldSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class NullWorldArchive : IWorldArchive
{
    public Task SaveAsync(WorldSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
