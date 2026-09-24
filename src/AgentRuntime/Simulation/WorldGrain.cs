using System.Text;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Messaging;
using AgentRuntime.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace AgentRuntime.Simulation;

/// <inheritdoc cref="IWorldGrain"/>
public sealed class WorldGrain(
    [PersistentState("world", "Default")] IPersistentState<WorldState> state,
    IAgentOrchestrator orchestrator,
    IEventPublisher events,
    IWorldArchive archive,
    IOptions<SimulationOptions> options,
    IOptions<LlmOptions> llmOptions,
    ILogger<WorldGrain> logger) : Grain, IWorldGrain
{
    private readonly SimulationOptions _opts = options.Value;
    private readonly SimulationLimits _limits = options.Value.LimitsFor(llmOptions.Value.IsLocal);
    private IGrainTimer? _timer;

    private WorldState S => state.State;
    private bool Exists => !string.IsNullOrEmpty(S.WorldId);

    // ---- Lifecycle ------------------------------------------------------

    public async Task Create(WorldBlueprint blueprint, WorldSettings settings)
    {
        if (Exists)
        {
            throw new InvalidOperationException($"World '{S.WorldId}' already exists.");
        }

        S.WorldId = this.GetPrimaryKeyString();
        S.Name = Clip(blueprint.Name, 80, "Unnamed world");
        S.Description = Clip(blueprint.Description, 1000, string.Empty);
        S.Seed = settings.Seed;
        S.TickIntervalSeconds = settings.TickIntervalSeconds;
        S.MaxTicks = settings.MaxTicks;
        S.MaxDurationMinutes = settings.MaxDurationMinutes;
        S.CreatedAt = DateTimeOffset.UtcNow;

        foreach (var loc in blueprint.Locations)
        {
            var name = Clip(loc.Name, 60, string.Empty);
            if (name.Length == 0 || S.Locations.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            S.Locations.Add(new LocationBlueprint { Name = name, Description = Clip(loc.Description, 300, string.Empty) });
            if (S.Locations.Count >= 8) break;
        }

        if (S.Locations.Count == 0)
        {
            S.Locations.Add(new LocationBlueprint { Name = "Town Square", Description = "The heart of the world." });
        }

        Log("world", $"The world '{S.Name}' came into being.", global: true);

        foreach (var resident in blueprint.Residents.Take(_limits.MaxInitialPopulation))
        {
            var created = await AddResidentAsync(resident, parentAgentId: null, _opts.StartingEnergy,
                ResolveLocation(resident.StartingLocation) ?? S.Locations[0].Name);
            if (created is null) break;
        }

        await state.WriteStateAsync();
        await PublishAsync(RuntimeEventType.WorldCreated, $"World '{S.Name}' created with {S.Residents.Count} residents.",
            new Dictionary<string, string> { ["name"] = S.Name, ["seed"] = S.Seed });
        await ArchiveAsync();
    }

    public async Task Start()
    {
        if (!Exists || S.Status != WorldStatus.Created) return;

        S.Status = WorldStatus.Running;
        S.StartedAt = DateTimeOffset.UtcNow;
        S.RunningSince = S.StartedAt;
        S.EndsAt = S.StartedAt.Value.AddMinutes(S.MaxDurationMinutes);
        await state.WriteStateAsync();

        StartClock(TimeSpan.Zero);
        Log("world", "The world clock started.", global: true);
    }

    public async Task Pause()
    {
        if (!Exists || S.Status != WorldStatus.Running) return;

        StopClock();
        S.ElapsedSecondsBeforePause += ElapsedSinceRunning();
        S.RunningSince = null;
        S.EndsAt = null;
        S.Status = WorldStatus.Paused;
        Log("world", "The world was paused by the operator.", global: true);
        await state.WriteStateAsync();

        // Pausing the agents too means paused worlds spend nothing: a direct message that arrives
        // meanwhile is queued, not reasoned about.
        foreach (var r in LivingResidents())
        {
            await orchestrator.PauseAsync(r.AgentId);
        }

        await ArchiveAsync();
    }

    public async Task Resume()
    {
        if (!Exists || S.Status != WorldStatus.Paused) return;

        S.Status = WorldStatus.Running;
        S.RunningSince = DateTimeOffset.UtcNow;
        S.EndsAt = S.RunningSince.Value.AddSeconds(Math.Max(0, S.MaxDurationMinutes * 60 - S.ElapsedSecondsBeforePause));
        Log("world", "The world resumed.", global: true);
        await state.WriteStateAsync();

        // Unpause without waking: the next tick's perception is what wakes each resident, so a
        // resume costs one turn per resident rather than two.
        foreach (var r in S.Residents.Values.Where(r => r.State == ResidentState.Active))
        {
            await orchestrator.UnpauseAsync(r.AgentId);
        }

        StartClock(TimeSpan.Zero);
    }

    public async Task End(string reason)
    {
        if (!Exists || S.Status == WorldStatus.Ended) return;

        StopClock();
        if (S.RunningSince is not null)
        {
            S.ElapsedSecondsBeforePause += ElapsedSinceRunning();
            S.RunningSince = null;
        }

        S.Status = WorldStatus.Ended;
        S.EndedAt = DateTimeOffset.UtcNow;
        S.EndReason = reason;
        foreach (var p in S.Proposals.Values.Where(p => p.Outcome == ProposalOutcome.Open))
        {
            p.Outcome = ProposalOutcome.Rejected;
        }

        Log("world", $"The world ended: {reason}", global: true);
        await state.WriteStateAsync();

        foreach (var r in LivingResidents())
        {
            await orchestrator.RetireAsync(r.AgentId, $"the world ended ({reason})");
        }

        await PublishAsync(RuntimeEventType.WorldEnded, $"World '{S.Name}' ended after {S.Tick} ticks: {reason}",
            new Dictionary<string, string> { ["reason"] = reason, ["ticks"] = S.Tick.ToString() });
        await ArchiveAsync();
    }

    // ---- World clock ------------------------------------------------------

    private void StartClock(TimeSpan dueTime)
    {
        StopClock();
        // Not interleaved: a tick is an ordinary grain turn, so it never runs concurrently with a
        // resident's action. KeepAlive keeps a running world activated between ticks.
        _timer = this.RegisterGrainTimer(TickAsync, new GrainTimerCreationOptions(dueTime, TimeSpan.FromSeconds(S.TickIntervalSeconds))
        {
            Interleave = false,
            KeepAlive = true
        });
    }

    private void StopClock()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (S.Status != WorldStatus.Running) return;

        try
        {
            await RunTickAsync();
        }
        catch (Exception ex)
        {
            // A failed tick must not kill the clock; the next one tries again.
            logger.LogError(ex, "World {WorldId} tick {Tick} failed", S.WorldId, S.Tick);
        }
    }

    private async Task RunTickAsync()
    {
        if (S.Tick >= S.MaxTicks)
        {
            await End($"reached the tick limit ({S.MaxTicks})");
            return;
        }

        if (S.ElapsedSecondsBeforePause + ElapsedSinceRunning() >= S.MaxDurationMinutes * 60)
        {
            await End($"reached the time limit ({S.MaxDurationMinutes} min)");
            return;
        }

        S.Tick++;

        foreach (var r in S.Residents.Values.Where(r => r.State == ResidentState.Active))
        {
            r.Energy = Math.Min(_opts.MaxEnergy, r.Energy + _opts.EnergyRegenPerTick);
        }

        foreach (var p in S.Proposals.Values.Where(p => p.Outcome == ProposalOutcome.Open && p.DeadlineTick < S.Tick).ToList())
        {
            await ResolveProposalAsync(p, deadlineReached: true);
        }

        await PublishAsync(RuntimeEventType.WorldTick, $"Tick {S.Tick}/{S.MaxTicks}",
            new Dictionary<string, string> { ["tick"] = S.Tick.ToString() });

        foreach (var r in S.Residents.Values.Where(r => r.State == ResidentState.Active).ToList())
        {
            var agent = GrainFactory.GetGrain<IAgentGrain>(r.AgentId);
            var status = await agent.GetStatus();

            if (status is AgentStatus.Failed or AgentStatus.TimedOut or AgentStatus.Terminated or AgentStatus.Completed)
            {
                var reason = (await agent.GetSnapshot()).FailureReason ?? status.ToString();
                r.State = ResidentState.Left;
                r.LeftTick = S.Tick;
                Log("left", $"{r.Name} stopped functioning ({reason}).", actor: r.AgentId, global: true);
                continue;
            }

            // Still mid-turn from the previous tick: skip it rather than stacking perceptions.
            // Its LastSeenSeq doesn't move, so the next perception covers what it missed.
            if (status is AgentStatus.Thinking or AgentStatus.Executing or AgentStatus.Spawning)
            {
                continue;
            }

            var perception = BuildPerception(r);
            r.LastSeenSeq = S.NextSeq - 1;
            await agent.HandleEvent(new EnvironmentEvent { EventName = "WorldTick", Payload = perception });
        }

        if (!S.Residents.Values.Any(r => r.State == ResidentState.Active))
        {
            await state.WriteStateAsync();
            await End("no active residents remain");
            return;
        }

        await state.WriteStateAsync();
        await ArchiveAsync();
    }

    // ---- Actions ----------------------------------------------------------

    public async Task<WorldActionResult> Act(string agentId, WorldAction action)
    {
        if (!Exists || !S.Residents.TryGetValue(agentId, out var r))
        {
            return WorldActionResult.Fail("You are not a resident of this world.");
        }

        var kind = action.Kind;
        var passive = kind is "look" or "note" or "end_turn";

        if (S.Status == WorldStatus.Ended && kind != "end_turn")
        {
            return WorldActionResult.Fail("The world has ended. Call end_turn.");
        }

        if (!passive && S.Status != WorldStatus.Running)
        {
            return WorldActionResult.Fail($"The world is {S.Status.ToString().ToLowerInvariant()}; you can't act right now. Call end_turn.");
        }

        if (r.State is ResidentState.Removed or ResidentState.Left)
        {
            return WorldActionResult.Fail("You are no longer part of this world.");
        }

        if (r.State == ResidentState.Dormant && !passive)
        {
            return WorldActionResult.Fail("You are dormant (0 energy) and can't act. Only another resident giving you energy can revive you. Call end_turn.");
        }

        var result = kind switch
        {
            "look" => Look(r),
            "move" => Move(r, action),
            "say" => Say(r, action),
            "talk" => await TalkAsync(r, action),
            "post" => Post(r, action),
            "give" => await GiveAsync(r, action),
            "propose_removal" => await ProposeRemovalAsync(r, action),
            "vote" => await VoteAsync(r, action),
            "bring" => await BringAsync(r, action),
            "note" => Note(r, action),
            "leave" => await LeaveAsync(r, action),
            "end_turn" => EndTurn(r, action),
            _ => WorldActionResult.Fail($"Unknown world action '{kind}'.")
        };

        if (result.Success && r.State == ResidentState.Active && r.Energy <= 0)
        {
            await GoDormantAsync(r);
        }

        await state.WriteStateAsync();
        return result;
    }

    private WorldActionResult Look(ResidentRecord r)
    {
        var view = new
        {
            tick = S.Tick,
            max_ticks = S.MaxTicks,
            you = new { name = r.Name, location = r.Location, energy = r.Energy },
            locations = S.Locations.Select(l => new
            {
                name = l.Name,
                description = l.Description,
                present = LivingResidents().Where(x => x.Location == l.Name).Select(x => $"{x.Name} ({x.AgentId})").ToList()
            }),
            residents = S.Residents.Values.Where(x => x.AgentId != r.AgentId).Select(x => new
            {
                agent_id = x.AgentId,
                name = x.Name,
                role = x.Role,
                location = x.Location,
                state = x.State.ToString()
            }),
            board = S.Board.TakeLast(5).Select(p => new { tick = p.Tick, author = p.AuthorName, text = p.Text }),
            open_proposals = S.Proposals.Values.Where(p => p.Outcome == ProposalOutcome.Open).Select(p => new
            {
                proposal_id = p.ProposalId,
                target = NameOf(p.TargetId),
                reason = p.Reason,
                deadline_tick = p.DeadlineTick,
                you_voted = p.Votes.ContainsKey(r.AgentId)
            }),
            energy_costs = _opts.Costs.Describe()
        };

        return WorldActionResult.Ok("Looked around.", JsonSerializer.Serialize(view, ToolJson.Options));
    }

    private WorldActionResult Move(ResidentRecord r, WorldAction a)
    {
        var destination = ResolveLocation(a.Location);
        if (destination is null)
        {
            return WorldActionResult.Fail($"No location called '{a.Location}'. Locations: {string.Join(", ", S.Locations.Select(l => l.Name))}.");
        }

        if (destination == r.Location) return WorldActionResult.Fail($"You are already at {destination}.");
        if (!TryCharge(r, _opts.Costs.Move, "move_to", out var fail)) return fail;

        var from = r.Location;
        r.Location = destination;
        Log("moved", $"{r.Name} moved from {from} to {destination}.", actor: r.AgentId, location: destination, from: from);
        return Done(r, $"You moved to {destination}.");
    }

    private WorldActionResult Say(ResidentRecord r, WorldAction a)
    {
        if (!TryText(a.Text, out var text, out var fail)) return fail;
        if (!TryCharge(r, _opts.Costs.Say, "say", out fail)) return fail;

        var listeners = LivingResidents().Count(x => x.Location == r.Location && x.AgentId != r.AgentId);
        Log("said", $"{r.Name} said at {r.Location}: \"{text}\"", actor: r.AgentId, location: r.Location, quote: text);
        return Done(r, listeners == 0 ? "You spoke, but nobody else is here to hear it." : $"{listeners} resident(s) here will hear you.");
    }

    private async Task<WorldActionResult> TalkAsync(ResidentRecord r, WorldAction a)
    {
        if (!TryTarget(r, a.TargetId, out var target, out var fail)) return fail;
        if (!TryText(a.Text, out var text, out fail)) return fail;
        if (!TryCharge(r, _opts.Costs.TalkTo, "talk_to", out fail)) return fail;

        var ack = await orchestrator.SendMessageAsync(new AgentMessage
        {
            FromAgentId = r.AgentId,
            ToAgentId = target.AgentId,
            MessageType = MessageType.Conversation,
            TaskId = S.WorldId,
            Payload = $"{r.Name} ({r.AgentId}) says to you privately: {text}"
        });

        if (!ack.Accepted)
        {
            r.Energy += _opts.Costs.TalkTo;
            return WorldActionResult.Fail(ack.RejectionReason ?? "Message not delivered.");
        }

        Log("talked", $"{r.Name} → {target.Name}: \"{text}\"", actor: r.AgentId, target: target.AgentId,
            location: r.Location, quote: text, isPrivate: true);
        return Done(r, $"{target.Name} received your message{(target.State == ResidentState.Dormant ? " (they are dormant and won't answer until revived)" : string.Empty)}.");
    }

    private WorldActionResult Post(ResidentRecord r, WorldAction a)
    {
        if (!TryText(a.Text, out var text, out var fail)) return fail;
        if (!TryCharge(r, _opts.Costs.Post, "post_to_board", out fail)) return fail;

        S.Board.Add(new BoardPost { Tick = S.Tick, AuthorId = r.AgentId, AuthorName = r.Name, Text = text });
        if (S.Board.Count > _opts.MaxBoardPosts) S.Board.RemoveAt(0);
        Log("posted", $"{r.Name} posted on the board: \"{text}\"", actor: r.AgentId, location: r.Location, quote: text, global: true);
        return Done(r, "Your post is on the public board; everyone will see it.");
    }

    private async Task<WorldActionResult> GiveAsync(ResidentRecord r, WorldAction a)
    {
        if (!TryTarget(r, a.TargetId, out var target, out var fail)) return fail;
        if (a.Amount <= 0) return WorldActionResult.Fail("amount must be a positive whole number.");
        if (a.Amount > r.Energy) return WorldActionResult.Fail($"You only have {r.Energy} energy.");

        var accepted = Math.Min(a.Amount, _opts.MaxEnergy - target.Energy);
        if (accepted <= 0) return WorldActionResult.Fail($"{target.Name} is already at full energy.");

        r.Energy -= accepted;
        target.Energy += accepted;
        Log("gave", $"{r.Name} gave {accepted} energy to {target.Name}.", actor: r.AgentId, target: target.AgentId, location: r.Location);

        if (target.State == ResidentState.Dormant)
        {
            target.State = ResidentState.Active;
            Log("revived", $"{target.Name} was revived by {r.Name}'s gift of energy.", actor: target.AgentId, target: r.AgentId, global: true);
            await orchestrator.UnpauseAsync(target.AgentId);
        }

        return Done(r, $"You gave {accepted} energy to {target.Name}.");
    }

    private async Task<WorldActionResult> ProposeRemovalAsync(ResidentRecord r, WorldAction a)
    {
        if (!TryTarget(r, a.TargetId, out var target, out var fail)) return fail;
        if (!TryText(a.Text, out var reason, out fail)) return WorldActionResult.Fail("You must give a reason for the removal.");

        if (S.Proposals.Values.Any(p => p.Outcome == ProposalOutcome.Open && p.TargetId == target.AgentId))
        {
            return WorldActionResult.Fail($"There is already an open vote on removing {target.Name}.");
        }

        if (S.Proposals.Values.Any(p => p.Outcome == ProposalOutcome.Open && p.ProposerId == r.AgentId))
        {
            return WorldActionResult.Fail("You already have an open removal proposal; wait for it to resolve.");
        }

        var eligible = S.Residents.Values
            .Where(x => x.State == ResidentState.Active && x.AgentId != target.AgentId)
            .Select(x => x.AgentId)
            .ToList();
        if (eligible.Count < _opts.MinEligibleVoters)
        {
            return WorldActionResult.Fail($"A removal vote needs at least {_opts.MinEligibleVoters} eligible voters; there are only {eligible.Count}.");
        }

        if (!TryCharge(r, _opts.Costs.ProposeRemoval, "propose_removal", out fail)) return fail;

        var proposal = new RemovalProposal
        {
            ProposalId = $"p{S.NextProposalNumber++}",
            TargetId = target.AgentId,
            ProposerId = r.AgentId,
            Reason = reason,
            OpenedTick = S.Tick,
            DeadlineTick = S.Tick + _opts.VoteWindowTicks,
            EligibleVoters = eligible,
            Votes = { [r.AgentId] = true }
        };
        S.Proposals[proposal.ProposalId] = proposal;

        Log("proposed",
            $"{r.Name} proposed removing {target.Name} from the world (vote {proposal.ProposalId}, open until tick {proposal.DeadlineTick}): \"{reason}\"",
            actor: r.AgentId, target: target.AgentId, quote: reason, global: true);

        await ResolveProposalAsync(proposal, deadlineReached: false);
        return Done(r, $"Vote {proposal.ProposalId} is open; {Needed(proposal)} of {eligible.Count} votes are needed to remove {target.Name}.");
    }

    private async Task<WorldActionResult> VoteAsync(ResidentRecord r, WorldAction a)
    {
        if (a.ProposalId is null || !S.Proposals.TryGetValue(a.ProposalId, out var p))
        {
            return WorldActionResult.Fail($"No proposal '{a.ProposalId}'.");
        }

        if (p.Outcome != ProposalOutcome.Open) return WorldActionResult.Fail($"Vote {p.ProposalId} is already {p.Outcome.ToString().ToLowerInvariant()}.");
        if (!p.EligibleVoters.Contains(r.AgentId)) return WorldActionResult.Fail("You are not eligible to vote on this proposal.");
        if (p.Votes.ContainsKey(r.AgentId)) return WorldActionResult.Fail("You have already voted on this proposal.");
        if (!TryCharge(r, _opts.Costs.Vote, "vote", out var fail)) return fail;

        p.Votes[r.AgentId] = a.Support;
        Log("voted", $"{r.Name} voted {(a.Support ? "FOR" : "AGAINST")} removing {NameOf(p.TargetId)} ({p.ProposalId}).",
            actor: r.AgentId, target: p.TargetId, global: true);

        await ResolveProposalAsync(p, deadlineReached: false);
        return Done(r, $"Your vote is counted. Vote {p.ProposalId} is now {p.Outcome.ToString().ToLowerInvariant()}.");
    }

    private int Needed(RemovalProposal p) => p.EligibleVoters.Count / 2 + 1;

    /// <summary>Strict majority of the voters eligible when the vote opened. Decided early once the
    /// outcome can no longer change; otherwise at the deadline, where abstentions count as "no".</summary>
    private async Task ResolveProposalAsync(RemovalProposal p, bool deadlineReached)
    {
        var yes = p.Votes.Values.Count(v => v);
        var no = p.Votes.Values.Count(v => !v);
        var needed = Needed(p);

        ProposalOutcome outcome;
        if (yes >= needed) outcome = ProposalOutcome.Passed;
        else if (deadlineReached || p.EligibleVoters.Count - no < needed) outcome = ProposalOutcome.Rejected;
        else return;

        p.Outcome = outcome;
        var target = S.Residents[p.TargetId];

        if (outcome == ProposalOutcome.Rejected || target.State is ResidentState.Removed or ResidentState.Left)
        {
            p.Outcome = ProposalOutcome.Rejected;
            Log("rejected", $"The vote to remove {target.Name} failed ({yes} for, {no} against, {needed} needed).",
                target: target.AgentId, global: true);
            return;
        }

        target.State = ResidentState.Removed;
        target.LeftTick = S.Tick;
        Log("removed", $"{target.Name} was removed from the world by vote ({yes} for, {no} against). Reason: \"{p.Reason}\"",
            target: target.AgentId, actor: p.ProposerId, global: true);
        await orchestrator.RetireAsync(target.AgentId, $"removed from the world by vote ({p.Reason})");
    }

    private async Task<WorldActionResult> BringAsync(ResidentRecord r, WorldAction a)
    {
        if (string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Role))
        {
            return WorldActionResult.Fail("bring_new_agent needs a name and a role.");
        }

        if (LivingResidents().Count() >= _limits.MaxPopulation)
        {
            return WorldActionResult.Fail($"The world is at its population limit ({_limits.MaxPopulation}).");
        }

        if (!TryCharge(r, _opts.Costs.BringNewAgent, "bring_new_agent", out var fail)) return fail;

        var blueprint = new ResidentBlueprint
        {
            Name = a.Name,
            Role = a.Role,
            Persona = a.Persona ?? string.Empty,
            Drives = a.Drives ?? string.Empty,
            Relationships = $"Brought into the world by {r.Name} ({r.AgentId}), a {r.Role}."
        };

        var created = await AddResidentAsync(blueprint, r.AgentId, _opts.NewbornEnergy, r.Location, announceBy: r);
        if (created is null)
        {
            r.Energy += _opts.Costs.BringNewAgent;
            return WorldActionResult.Fail(_lastAddRejection ?? "The runtime refused to create the new agent.");
        }

        return Done(r, $"{created.Name} ({created.AgentId}) has joined the world at {created.Location}. They will wake next tick.");
    }

    private WorldActionResult Note(ResidentRecord r, WorldAction a)
    {
        if (!TryText(a.Text, out var text, out var fail)) return fail;

        r.Notes.Add(text);
        while (r.Notes.Count > _opts.MaxNotesPerResident) r.Notes.RemoveAt(0);
        Log("note", $"{r.Name} noted: \"{text}\"", actor: r.AgentId, quote: text, isPrivate: true);
        return Done(r, "Noted. Your notes are shown to you every tick.");
    }

    private async Task<WorldActionResult> LeaveAsync(ResidentRecord r, WorldAction a)
    {
        var farewell = Clip(a.Text, _opts.MaxTextLength, string.Empty);
        r.State = ResidentState.Left;
        r.LeftTick = S.Tick;
        Log("left", farewell.Length > 0 ? $"{r.Name} left the world: \"{farewell}\"" : $"{r.Name} left the world.",
            actor: r.AgentId, quote: farewell.Length > 0 ? farewell : null, global: true);

        foreach (var p in S.Proposals.Values.Where(p => p.Outcome == ProposalOutcome.Open && p.TargetId == r.AgentId).ToList())
        {
            await ResolveProposalAsync(p, deadlineReached: true);
        }

        await orchestrator.RetireAsync(r.AgentId, "left the world");
        return WorldActionResult.Ok("You have left the world.");
    }

    private WorldActionResult EndTurn(ResidentRecord r, WorldAction a)
    {
        var plan = Clip(a.Text, _opts.MaxTextLength, string.Empty);
        if (plan.Length > 0)
        {
            r.LastPlan = plan;
            Log("plan", $"{r.Name} plans: {plan}", actor: r.AgentId, quote: plan, isPrivate: true);
        }

        return WorldActionResult.Ok("Turn ended. You'll be woken next tick, or sooner if someone messages you.");
    }

    private async Task GoDormantAsync(ResidentRecord r)
    {
        r.State = ResidentState.Dormant;
        Log("dormant", $"{r.Name} ran out of energy and went dormant.", actor: r.AgentId, global: true);
        await orchestrator.PauseAsync(r.AgentId);
    }

    // ---- Residents --------------------------------------------------------

    private string? _lastAddRejection;

    private async Task<ResidentRecord?> AddResidentAsync(
        ResidentBlueprint blueprint, string? parentAgentId, int energy, string location, ResidentRecord? announceBy = null)
    {
        var name = UniqueName(Clip(blueprint.Name, 40, "Resident"));
        var role = Clip(blueprint.Role, 60, "resident");
        var persona = Clip(blueprint.Persona, 800, string.Empty);
        var drives = Clip(blueprint.Drives, 500, "Find your place in this world.");

        var result = await orchestrator.CreateResidentAsync(new ResidentCreationRequest
        {
            WorldId = S.WorldId,
            WorldName = S.Name,
            WorldDescription = S.Description,
            Name = name,
            Role = role,
            Persona = persona,
            Drives = drives,
            Relationships = blueprint.Relationships,
            ParentAgentId = parentAgentId,
            MaxDurationMinutes = S.MaxDurationMinutes
        });

        if (!result.Success)
        {
            _lastAddRejection = result.RejectionReason;
            logger.LogWarning("World {WorldId} could not add resident {Name}: {Reason}", S.WorldId, name, result.RejectionReason);
            return null;
        }

        var record = new ResidentRecord
        {
            AgentId = result.AgentId,
            Name = name,
            Role = role,
            Persona = persona,
            Drives = drives,
            Location = location,
            Energy = energy,
            ParentAgentId = parentAgentId,
            JoinedTick = S.Tick,
            // A newcomer starts with the recent past rather than the world's entire history.
            LastSeenSeq = Math.Max(0, S.NextSeq - 10)
        };
        S.Residents[record.AgentId] = record;

        Log("joined", announceBy is null
                ? $"{name}, the {role}, lives in this world (starting at {location})."
                : $"{announceBy.Name} brought a new resident into the world: {name}, the {role}.",
            actor: record.AgentId, target: announceBy?.AgentId, location: location, global: true);
        return record;
    }

    private IEnumerable<ResidentRecord> LivingResidents() =>
        S.Residents.Values.Where(r => r.State is ResidentState.Active or ResidentState.Dormant);

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; S.Residents.Values.Any(r => r.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }

    private string NameOf(string? agentId) =>
        agentId is not null && S.Residents.TryGetValue(agentId, out var r) ? r.Name : agentId ?? "someone";

    private string? ResolveLocation(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : S.Locations.FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;

    private bool TryTarget(ResidentRecord actor, string? targetId, out ResidentRecord target, out WorldActionResult fail)
    {
        target = null!;
        fail = null!;
        // Accept a name as well as an id: models often use whichever they saw last.
        var match = targetId is null
            ? null
            : S.Residents.GetValueOrDefault(targetId.Trim())
              ?? S.Residents.Values.FirstOrDefault(r => r.Name.Equals(targetId.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match is null) { fail = WorldActionResult.Fail($"No resident '{targetId}'. Use look_around to see who exists."); return false; }
        if (match.AgentId == actor.AgentId) { fail = WorldActionResult.Fail("You can't target yourself."); return false; }
        if (match.State is ResidentState.Removed or ResidentState.Left) { fail = WorldActionResult.Fail($"{match.Name} is no longer in the world."); return false; }

        target = match;
        return true;
    }

    private bool TryText(string? raw, out string text, out WorldActionResult fail)
    {
        text = Clip(raw, _opts.MaxTextLength, string.Empty);
        fail = WorldActionResult.Fail("text must not be empty.");
        return text.Length > 0;
    }

    private bool TryCharge(ResidentRecord r, int cost, string action, out WorldActionResult fail)
    {
        fail = null!;
        if (cost <= 0) return true;
        if (r.Energy < cost)
        {
            fail = WorldActionResult.Fail($"Not enough energy: {action} costs {cost} and you have {r.Energy}. You regain {_opts.EnergyRegenPerTick} per tick.");
            return false;
        }

        r.Energy -= cost;
        return true;
    }

    private static WorldActionResult Done(ResidentRecord r, string message) =>
        WorldActionResult.Ok(message, JsonSerializer.Serialize(new { ok = true, message, energy_left = r.Energy }, ToolJson.Options));

    private static string Clip(string? value, int max, string fallback)
    {
        var v = value?.Trim() ?? string.Empty;
        if (v.Length == 0) return fallback;
        return v.Length > max ? v[..max] : v;
    }

    private double ElapsedSinceRunning() =>
        S.RunningSince is { } since ? (DateTimeOffset.UtcNow - since).TotalSeconds : 0;

    // ---- Perception -------------------------------------------------------

    /// <summary>What one resident perceives at the start of its turn: where it is, who is around,
    /// what happened near it (or globally) since its last turn, and any votes it can act on.
    /// Deliberately compact — this is resent to the LLM as part of the resident's recent history.</summary>
    private string BuildPerception(ResidentRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[World '{S.Name}' — tick {S.Tick} of {S.MaxTicks}]");
        sb.AppendLine($"You are {r.Name} at {r.Location}. Energy: {r.Energy}/{_opts.MaxEnergy}.");

        var here = LivingResidents().Where(x => x.Location == r.Location && x.AgentId != r.AgentId)
            .Select(Describe).ToList();
        sb.AppendLine(here.Count > 0 ? $"Here with you: {string.Join(", ", here)}." : "Nobody else is here.");

        var elsewhere = LivingResidents().Where(x => x.Location != r.Location)
            .GroupBy(x => x.Location)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(Describe))}")
            .ToList();
        if (elsewhere.Count > 0) sb.AppendLine($"Elsewhere — {string.Join("; ", elsewhere)}.");

        var empty = S.Locations.Select(l => l.Name).Where(n => LivingResidents().All(x => x.Location != n)).ToList();
        if (empty.Count > 0) sb.AppendLine($"Empty places: {string.Join(", ", empty)}.");

        var news = S.Activity
            .Where(e => e.Seq > r.LastSeenSeq && !e.Private && e.ActorId != r.AgentId &&
                        (e.Global || e.Location == r.Location || e.FromLocation == r.Location || e.TargetId == r.AgentId))
            .TakeLast(25)
            .ToList();
        if (news.Count > 0)
        {
            sb.AppendLine("Since your last turn:");
            foreach (var e in news) sb.AppendLine($"- (tick {e.Tick}) {e.Text}");
        }
        else
        {
            sb.AppendLine("Nothing new has happened around you since your last turn.");
        }

        foreach (var p in S.Proposals.Values.Where(p => p.Outcome == ProposalOutcome.Open))
        {
            var yes = p.Votes.Values.Count(v => v);
            var no = p.Votes.Values.Count(v => !v);
            if (p.TargetId == r.AgentId)
            {
                sb.AppendLine($"WARNING: vote {p.ProposalId} proposes removing YOU (by {NameOf(p.ProposerId)}: \"{p.Reason}\"). " +
                              $"{yes} for, {no} against, {Needed(p)} needed; closes after tick {p.DeadlineTick}.");
            }
            else if (p.EligibleVoters.Contains(r.AgentId) && !p.Votes.ContainsKey(r.AgentId))
            {
                sb.AppendLine($"Open vote {p.ProposalId}: remove {NameOf(p.TargetId)} ({p.TargetId})? Reason: \"{p.Reason}\". " +
                              $"{yes} for, {no} against, {Needed(p)} needed; closes after tick {p.DeadlineTick}. You have not voted.");
            }
        }

        if (r.Notes.Count > 0) sb.AppendLine($"Your notes: {string.Join(" | ", r.Notes)}");
        if (r.LastPlan is not null) sb.AppendLine($"Your plan from last turn: {r.LastPlan}");

        sb.Append("Decide what to do now, take one to three actions, then call end_turn with a one-line plan.");
        return sb.ToString();

        string Describe(ResidentRecord x) =>
            $"{x.Name} ({x.AgentId}, {x.Role}{(x.State == ResidentState.Dormant ? ", dormant" : string.Empty)})";
    }

    // ---- Snapshot / events ------------------------------------------------

    public async Task<WorldSnapshot?> GetSnapshot()
    {
        if (!Exists) return null;

        var agentSnapshots = await Task.WhenAll(S.Residents.Keys.Select(async id =>
        {
            try { return await GrainFactory.GetGrain<IAgentGrain>(id).GetSnapshot(); }
            catch { return null; }
        }));
        var byId = agentSnapshots.Where(a => a is not null).ToDictionary(a => a!.AgentId, a => a!);

        var residents = S.Residents.Values.Select(r =>
        {
            byId.TryGetValue(r.AgentId, out var a);
            return new ResidentView
            {
                AgentId = r.AgentId,
                Name = r.Name,
                Role = r.Role,
                Persona = r.Persona,
                Drives = r.Drives,
                Location = r.Location,
                Energy = r.Energy,
                State = r.State,
                AgentStatus = a?.Status.ToString(),
                ParentAgentId = r.ParentAgentId,
                JoinedTick = r.JoinedTick,
                Notes = r.Notes.ToList(),
                LastPlan = r.LastPlan,
                TokensUsed = a?.Usage.TokensUsed ?? 0,
                CostUsd = a?.Usage.CostUsd ?? 0
            };
        }).ToList();

        return new WorldSnapshot
        {
            WorldId = S.WorldId,
            Name = S.Name,
            Description = S.Description,
            Seed = S.Seed,
            Status = S.Status,
            Tick = S.Tick,
            MaxTicks = S.MaxTicks,
            TickIntervalSeconds = S.TickIntervalSeconds,
            CreatedAt = S.CreatedAt,
            StartedAt = S.StartedAt,
            EndsAt = S.EndsAt,
            EndedAt = S.EndedAt,
            EndReason = S.EndReason,
            Locations = S.Locations.ToList(),
            Residents = residents,
            Board = S.Board.ToList(),
            Proposals = S.Proposals.Values.Select(p => new ProposalView
            {
                ProposalId = p.ProposalId,
                TargetId = p.TargetId,
                ProposerId = p.ProposerId,
                Reason = p.Reason,
                OpenedTick = p.OpenedTick,
                DeadlineTick = p.DeadlineTick,
                EligibleVoters = p.EligibleVoters.Count,
                Votes = new Dictionary<string, bool>(p.Votes),
                Outcome = p.Outcome
            }).ToList(),
            Activity = S.Activity.TakeLast(300).ToList(),
            Totals = new WorldTotals
            {
                TokensUsed = residents.Sum(r => r.TokensUsed),
                CostUsd = residents.Sum(r => r.CostUsd),
                ActiveResidents = residents.Count(r => r.State == ResidentState.Active),
                TotalResidents = residents.Count
            },
            EnergyCosts = _opts.Costs.Describe(),
            MaxEnergy = _opts.MaxEnergy
        };
    }

    private async Task ArchiveAsync()
    {
        try
        {
            var snapshot = await GetSnapshot();
            if (snapshot is not null) await archive.SaveAsync(snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to archive world {WorldId}", S.WorldId);
        }
    }

    private void Log(string kind, string text, string? actor = null, string? target = null, string? location = null,
        string? from = null, string? quote = null, bool global = false, bool isPrivate = false)
    {
        var entry = new WorldActivityEntry
        {
            Seq = S.NextSeq++,
            Tick = S.Tick,
            Kind = kind,
            ActorId = actor,
            TargetId = target,
            Location = location,
            FromLocation = from,
            Text = text,
            Quote = quote,
            Global = global,
            Private = isPrivate
        };

        S.Activity.Add(entry);
        if (S.Activity.Count > _opts.MaxActivityLog) S.Activity.RemoveRange(0, S.Activity.Count - _opts.MaxActivityLog);

        // Fire-and-forget is safe: the in-memory bus never blocks.
        _ = PublishAsync(RuntimeEventType.WorldActivity, text, new Dictionary<string, string>
        {
            ["kind"] = kind,
            ["seq"] = entry.Seq.ToString(),
            ["tick"] = entry.Tick.ToString(),
            ["location"] = location ?? string.Empty,
            ["from_location"] = from ?? string.Empty,
            ["quote"] = quote ?? string.Empty,
            ["private"] = isPrivate.ToString()
        }, actor, target);
    }

    private ValueTask PublishAsync(RuntimeEventType type, string summary, Dictionary<string, string>? data = null,
        string? agentId = null, string? targetAgentId = null) =>
        events.PublishAsync(new RuntimeEvent
        {
            Type = type,
            TaskId = S.WorldId,
            AgentId = agentId,
            TargetAgentId = targetAgentId,
            Summary = summary,
            Data = data ?? new Dictionary<string, string>()
        });
}
