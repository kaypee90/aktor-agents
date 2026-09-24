using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Simulation;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Living-world simulation against real grains: the world clock waking residents, residents acting
/// through world tools, vote-based removal, and births — all with a scripted LLM.
/// </summary>
public sealed class WorldSimulationTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private static readonly Regex NameRegex = new(@"Your name is ([^.]+)\.");

    private static string ResidentName(LlmCompletionRequest request) =>
        NameRegex.Match(request.Messages[0].Content ?? string.Empty).Groups[1].Value;

    private static string LastUserText(LlmCompletionRequest request) =>
        request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;

    private static bool IsPerception(LlmCompletionRequest request) =>
        request.Messages[^1].Role == ChatRole.User && LastUserText(request).Contains("[World '");

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static LlmCompletionResponse Respond(params ToolCall[] calls) => new() { ToolCalls = calls, FinishReason = LlmFinishReason.ToolCalls };

    private static LlmCompletionResponse EndTurn(string plan = "Wait.") => Respond(Call("end_turn", new { plan }));

    private static WorldBlueprint Blueprint(params string[] names) => new()
    {
        Name = "Testville",
        Description = "A test world.",
        Locations = [new LocationBlueprint { Name = "Square" }, new LocationBlueprint { Name = "Market" }],
        Residents = names.Select(n => new ResidentBlueprint { Name = n, Role = "tester", Drives = "Pass the test.", StartingLocation = "Square" }).ToList()
    };

    private async Task<IWorldGrain> CreateWorldAsync(WorldBlueprint blueprint, int maxTicks)
    {
        var world = _cluster.GrainFactory.GetGrain<IWorldGrain>(WorldIds.New());
        await world.Create(blueprint, new WorldSettings { Seed = "test", TickIntervalSeconds = 1, MaxTicks = maxTicks, MaxDurationMinutes = 5 });
        await world.Start();
        return world;
    }

    private static async Task<WorldSnapshot> WaitForAsync(IWorldGrain world, Func<WorldSnapshot, bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        WorldSnapshot? snapshot = null;
        while (DateTime.UtcNow < deadline)
        {
            snapshot = await world.GetSnapshot();
            if (snapshot is not null && condition(snapshot)) return snapshot;
            await Task.Delay(200);
        }

        throw new TimeoutException(snapshot is null
            ? "World snapshot was null."
            : $"World condition not met (status {snapshot.Status}, tick {snapshot.Tick}). Activity:\n{string.Join("\n", snapshot.Activity.Select(a => $"[{a.Kind}] {a.Text}"))}");
    }

    [Fact]
    public async Task Residents_AreWokenByTicks_AndActThroughWorldTools()
    {
        ScriptedLlmProviderRegistry.Current = request =>
            IsPerception(request)
                ? Respond(Call("say", new { text = $"Hello from {ResidentName(request)}" }), Call("end_turn", new { plan = "Keep chatting." }))
                : EndTurn();

        var world = await CreateWorldAsync(Blueprint("Ann", "Ben", "Cy"), maxTicks: 2);
        var ended = await WaitForAsync(world, s => s.Status == WorldStatus.Ended);

        foreach (var name in new[] { "Ann", "Ben", "Cy" })
        {
            Assert.Contains(ended.Activity, a => a.Kind == "said" && a.Quote == $"Hello from {name}");
        }

        Assert.Contains(ended.Activity, a => a.Kind == "plan");
        Assert.Contains("tick limit", ended.EndReason);
        // Ending the world retires every resident's agent.
        var statuses = await Task.WhenAll(ended.Residents.Select(r => _cluster.GrainFactory.GetGrain<IAgentGrain>(r.AgentId).GetStatus()));
        Assert.All(statuses, s => Assert.Equal(AgentStatus.Terminated, s));
    }

    [Fact]
    public async Task RemovalVote_WithMajority_RemovesTheResident()
    {
        string? danId = null;
        ScriptedLlmProviderRegistry.Current = request =>
        {
            if (!IsPerception(request)) return EndTurn();

            var name = ResidentName(request);
            var text = LastUserText(request);
            danId ??= Regex.Match(text, @"Dan \((res-[0-9a-f]+)").Groups[1].Value is { Length: > 0 } id ? id : null;

            var openVote = Regex.Match(text, @"Open vote (p\d+):");
            if (openVote.Success && name != "Dan")
            {
                return Respond(Call("vote", new { proposal_id = openVote.Groups[1].Value, support = true }), Call("end_turn", new { plan = "Voted." }));
            }

            if (name == "Ann" && danId is not null && !text.Contains("vote p1"))
            {
                return Respond(Call("propose_removal", new { agent_id = danId, reason = "Dan keeps lying." }), Call("end_turn", new { plan = "Wait for the vote." }));
            }

            return EndTurn();
        };

        var world = await CreateWorldAsync(Blueprint("Ann", "Ben", "Cy", "Dan"), maxTicks: 10);
        var snapshot = await WaitForAsync(world, s => s.Residents.Any(r => r.Name == "Dan" && r.State == ResidentState.Removed));

        var dan = snapshot.Residents.Single(r => r.Name == "Dan");
        Assert.Contains(snapshot.Proposals, p => p.TargetId == dan.AgentId && p.Outcome == ProposalOutcome.Passed);
        Assert.Contains(snapshot.Activity, a => a.Kind == "removed" && a.TargetId == dan.AgentId);

        await WaitForAgentStatusAsync(dan.AgentId, AgentStatus.Terminated);
        await world.End("test over");
    }

    [Fact]
    public async Task Removal_IsRejected_WithTooFewVoters()
    {
        string? result = null;
        ScriptedLlmProviderRegistry.Current = request =>
        {
            if (IsPerception(request) && ResidentName(request) == "Ann")
            {
                var benId = Regex.Match(LastUserText(request), @"Ben \((res-[0-9a-f]+)").Groups[1].Value;
                return Respond(Call("propose_removal", new { agent_id = benId, reason = "No reason." }));
            }

            if (request.Messages[^1].Role == ChatRole.Tool && ResidentName(request) == "Ann")
            {
                result ??= request.Messages[^1].Content;
            }

            return EndTurn();
        };

        // Two residents: the proposer would be the only eligible voter, below MinEligibleVoters.
        var world = await CreateWorldAsync(Blueprint("Ann", "Ben"), maxTicks: 3);
        await WaitForAsync(world, s => s.Status == WorldStatus.Ended);

        Assert.NotNull(result);
        Assert.Contains("at least", result);
        var snapshot = await world.GetSnapshot();
        Assert.All(snapshot!.Residents, r => Assert.Equal(ResidentState.Active, r.State));
    }

    [Fact]
    public async Task BringNewAgent_CreatesAResidentWithLineage_AndChargesEnergy()
    {
        var brought = false;
        ScriptedLlmProviderRegistry.Current = request =>
        {
            if (IsPerception(request) && ResidentName(request) == "Ann" && !brought)
            {
                brought = true;
                return Respond(
                    Call("bring_new_agent", new { name = "Zed", role = "apprentice", persona = "Eager.", drives = "Learn." }),
                    Call("end_turn", new { plan = "Teach Zed." }));
            }

            return EndTurn();
        };

        var world = await CreateWorldAsync(Blueprint("Ann", "Ben"), maxTicks: 20);
        var snapshot = await WaitForAsync(world, s => s.Residents.Any(r => r.Name == "Zed"));

        var ann = snapshot.Residents.Single(r => r.Name == "Ann");
        var zed = snapshot.Residents.Single(r => r.Name == "Zed");
        Assert.Equal(ann.AgentId, zed.ParentAgentId);
        Assert.Equal(ann.Location, zed.Location);
        Assert.True(ann.Energy < 100 - 40 + 5, $"Ann should have paid for the new resident (energy {ann.Energy}).");

        var zedAgent = await _cluster.GrainFactory.GetGrain<IAgentGrain>(zed.AgentId).GetSnapshot();
        Assert.Equal(ann.AgentId, zedAgent.ParentAgentId);
        Assert.Equal(snapshot.WorldId, zedAgent.WorldId);
        // Residents never receive task-agent tools such as the filesystem or spawn_agent.
        Assert.DoesNotContain("spawn_agent", zedAgent.AllowedTools);
        Assert.Contains("end_turn", zedAgent.AllowedTools);

        await world.End("test over");
    }

    [Fact]
    public async Task TalkTo_DeliversPrivately_AndWakesTheRecipient()
    {
        ScriptedLlmProviderRegistry.Current = request =>
        {
            var name = ResidentName(request);
            var last = LastUserText(request);
            if (IsPerception(request) && name == "Ann" && request.Messages.Count(m => m.Role == ChatRole.User) == 1)
            {
                var benId = Regex.Match(last, @"Ben \((res-[0-9a-f]+)").Groups[1].Value;
                return Respond(Call("talk_to", new { agent_id = benId, text = "psst, Ben" }), Call("end_turn", new { plan = "Wait." }));
            }

            // The DM and a tick perception can arrive in the same turn under load, so look at every
            // user message rather than only the last one.
            var heardSecret = request.Messages.Any(m => m.Role == ChatRole.User && m.Content?.Contains("says to you privately: psst, Ben") == true);
            var alreadyToldIt = request.Messages.Any(m => m.ToolCalls?.Any(c => c.ArgumentsJson.Contains("secret")) == true);
            if (name == "Ben" && request.Messages[^1].Role == ChatRole.User && heardSecret && !alreadyToldIt)
            {
                return Respond(Call("say", new { text = "Ann told me a secret" }), Call("end_turn", new { plan = "Keep it." }));
            }

            return EndTurn();
        };

        var world = await CreateWorldAsync(Blueprint("Ann", "Ben", "Cy"), maxTicks: 20);
        var snapshot = await WaitForAsync(world, s => s.Activity.Any(a => a.Quote == "Ann told me a secret"));

        var talk = Assert.Single(snapshot.Activity, a => a.Kind == "talked");
        Assert.True(talk.Private);
        await world.End("test over");
    }

    private async Task WaitForAgentStatusAsync(string agentId, AgentStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await _cluster.GrainFactory.GetGrain<IAgentGrain>(agentId).GetStatus() == status) return;
            await Task.Delay(100);
        }

        Assert.Fail($"Agent {agentId} never reached {status}.");
    }
}
