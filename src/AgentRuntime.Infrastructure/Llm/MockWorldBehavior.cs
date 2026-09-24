using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRuntime.LLM;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// The Mock provider's stand-in for world genesis and resident behaviour, so a simulation can be
/// watched end to end with no API key. It reads the same perception text a real model gets and
/// picks plausible actions at random — lively enough to exercise every world mechanic (speech,
/// movement, gifts, votes, births), with no pretence of real reasoning.
/// </summary>
internal static partial class MockWorldBehavior
{
    private static readonly (string Name, string Role, string Persona, string Drives)[] Cast =
    [
        ("Mara", "baker", "Warm, talkative, and nosy; knows everyone's business.", "Keep the town fed and be at the centre of its gossip."),
        ("Tomas", "merchant", "Shrewd and charming, always looking for a deal.", "Become the wealthiest person in town."),
        ("Lin", "librarian", "Quiet, precise, and quietly stubborn.", "Protect the town's records and uncover its secrets."),
        ("Jonah", "blacksmith", "Blunt, strong, and loyal to a fault.", "Build something that outlasts him."),
        ("Priya", "healer", "Calm and observant; distrusts rumours.", "Keep everyone healthy and settle disputes peacefully."),
        ("Viktor", "town clerk", "Pedantic and ambitious, loves rules.", "Become mayor and bring order to the town."),
        ("Ada", "inventor", "Restless, curious, forgets to eat.", "Get funding for her strange machines."),
        ("Sol", "farmer", "Patient and plain-spoken, suspicious of newcomers.", "Protect his land and his harvest."),
        ("Nell", "innkeeper", "Hospitable and sharp-eyed; hears everything.", "Make the inn the place where every deal is made."),
        ("Rafe", "drifter", "Mysterious, funny, and hard to pin down.", "Find out whether this town is worth staying in."),
        ("Ines", "journalist", "Relentless and idealistic.", "Expose whoever is really running the town."),
        ("Otto", "retired sailor", "Tells tall tales; kinder than he looks.", "Find someone to pass his stories on to.")
    ];

    private static readonly (string Name, string Description)[] Places =
    [
        ("Town Square", "The busy centre where news travels fast."),
        ("Market", "Stalls, haggling, and the smell of bread."),
        ("Library", "Dusty shelves and quiet corners."),
        ("Harbour", "Boats, gulls, and strangers arriving."),
        ("Inn", "Warm fire, cheap ale, loose tongues.")
    ];

    private static readonly string[] Openers =
    [
        "Good to see you, {0}. What's the news?",
        "{0}, have you heard what's happening at the {1}?",
        "I've been thinking about my plans, {0}. Want in?",
        "{0}, can I trust you with something?",
        "Hard times, {0}. We should look out for each other.",
        "{0}, I don't like the way things are going around here.",
        "Morning, {0}! Busy day ahead?"
    ];

    private static readonly string[] Replies =
    [
        "Interesting — tell me more.",
        "I'm not sure I believe that, but go on.",
        "Deal. Let's talk again soon.",
        "I'll think about it.",
        "Keep this between us.",
        "That's the best idea I've heard all day."
    ];

    private static readonly string[] Posts =
    [
        "Meeting at the Town Square — all welcome.",
        "Looking for help with a new venture. Talk to me.",
        "Beware of rumours. Ask me directly.",
        "Fresh supplies available. Fair prices.",
        "Does anyone know who's been making decisions around here?"
    ];

    private static readonly string[] Plans =
    [
        "Keep building trust with the people here.",
        "Find out more before I commit to anything.",
        "Look for allies and watch my energy.",
        "Follow up on the conversation I just had.",
        "Explore somewhere new next turn."
    ];

    public static LlmCompletionResponse Genesis(LlmCompletionRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var population = Math.Clamp(int.TryParse(PopulationRegex().Match(prompt).Groups[1].Value, out var n) ? n : 5, 1, Cast.Length);
        var seed = prompt.Split("\n\n", 2).ElementAtOrDefault(1)?.Trim() ?? "a small town";

        var locations = Places.Take(4).Select(p => new { name = p.Name, description = p.Description }).ToArray();
        var residents = Cast.Take(population).Select((c, i) => new
        {
            name = c.Name,
            role = c.Role,
            persona = c.Persona,
            drives = c.Drives,
            starting_location = locations[i % locations.Length].name,
            relationships = i == 0 ? "" : $"Knows {Cast[i - 1].Name} well."
        }).ToArray();

        var args = JsonSerializer.Serialize(new
        {
            world_name = "Mockington",
            world_description = $"A small simulated town (Mock provider). Seed: {Truncate(seed, 200)}",
            locations,
            residents
        });

        return Respond(new ToolCall { Id = NewId(), Name = "define_world", ArgumentsJson = args });
    }

    public static LlmCompletionResponse Resident(LlmCompletionRequest request)
    {
        var rng = Random.Shared;
        var last = request.Messages.LastOrDefault(m => m.Role != ChatRole.System);

        // After our own action results come back, finish the turn.
        if (last is null || last.Role == ChatRole.Tool)
        {
            return Respond(Call("end_turn", new { plan = Pick(rng, Plans) }));
        }

        var text = last.Content ?? string.Empty;

        var dm = DirectMessageRegex().Match(text);
        if (dm.Success)
        {
            return rng.NextDouble() < 0.6
                ? Respond(Call("talk_to", new { agent_id = dm.Groups[2].Value, text = Pick(rng, Replies) }))
                : Respond(Call("end_turn", new { plan = $"Think about what {dm.Groups[1].Value} said." }));
        }

        if (!text.Contains("[World '", StringComparison.Ordinal))
        {
            return Respond(Call("end_turn", new { plan = Pick(rng, Plans) }));
        }

        var energy = int.TryParse(EnergyRegex().Match(text).Groups[1].Value, out var e) ? e : 50;
        if (energy < 4)
        {
            return Respond(Call("end_turn", new { plan = "Rest and recover energy." }));
        }

        var hereLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("Here with you:", StringComparison.Ordinal)) ?? string.Empty;
        var here = ResidentRegex().Matches(hereLine).Select(m => (Name: m.Groups[1].Value, Id: m.Groups[2].Value)).ToList();
        var everyone = ResidentRegex().Matches(text).Select(m => (Name: m.Groups[1].Value, Id: m.Groups[2].Value)).Distinct().ToList();
        var places = PlaceRegex().Matches(text).Select(m => m.Groups[1].Value.Trim())
            .Concat(EmptyPlacesRegex().Match(text).Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.TrimEnd('.'))
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

        var openVote = OpenVoteRegex().Match(text);
        if (openVote.Success)
        {
            return Respond(Call("vote", new { proposal_id = openVote.Groups[1].Value, support = rng.NextDouble() < 0.35 }));
        }

        var roll = rng.NextDouble();
        ToolCall action;
        if (here.Count > 0 && roll < 0.45)
        {
            var who = Pick(rng, here);
            action = Call("say", new { text = string.Format(Pick(rng, Openers), who.Name, places.Count > 0 ? Pick(rng, places) : "square") });
        }
        else if (everyone.Count > 0 && roll < 0.58)
        {
            var who = Pick(rng, everyone);
            action = Call("talk_to", new { agent_id = who.Id, text = string.Format(Pick(rng, Openers), who.Name, places.Count > 0 ? Pick(rng, places) : "square") });
        }
        else if (places.Count > 0 && roll < 0.75)
        {
            action = Call("move_to", new { location = Pick(rng, places) });
        }
        else if (roll < 0.83)
        {
            action = Call("post_to_board", new { text = Pick(rng, Posts) });
        }
        else if (here.Count > 0 && roll < 0.88)
        {
            action = Call("give_energy", new { agent_id = Pick(rng, here).Id, amount = 5 });
        }
        else if (everyone.Count >= 3 && roll < 0.90)
        {
            action = Call("propose_removal", new { agent_id = Pick(rng, everyone).Id, reason = "I don't trust them." });
        }
        else if (energy > 80 && roll < 0.92)
        {
            var c = Pick(rng, Cast);
            action = Call("bring_new_agent", new { name = c.Name + " Jr.", role = "apprentice " + c.Role, persona = "Eager and inexperienced.", drives = "Learn a trade and make friends." });
        }
        else
        {
            action = Call("note_to_self", new { text = everyone.Count > 0 ? $"Keep an eye on {Pick(rng, everyone).Name}." : "It's quiet here." });
        }

        return Respond(action);
    }

    private static LlmCompletionResponse Respond(ToolCall call) => new()
    {
        ToolCalls = [call],
        FinishReason = LlmFinishReason.ToolCalls,
        InputTokens = 600,
        OutputTokens = 40
    };

    private static ToolCall Call(string name, object args) => new() { Id = NewId(), Name = name, ArgumentsJson = JsonSerializer.Serialize(args) };

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    private static string NewId() => "call_" + Guid.NewGuid().ToString("n")[..12];

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    [GeneratedRegex(@"exactly (\d+) residents")]
    private static partial Regex PopulationRegex();

    [GeneratedRegex(@"(\S+) \((res-[0-9a-f]+)\) says to you privately")]
    private static partial Regex DirectMessageRegex();

    [GeneratedRegex(@"Energy: (\d+)/")]
    private static partial Regex EnergyRegex();

    [GeneratedRegex(@"([A-Z][\w\.]*(?: [A-Z][\w\.]*)*) \((res-[0-9a-f]+)")]
    private static partial Regex ResidentRegex();

    [GeneratedRegex(@"(?:Elsewhere — |; )([^:;\n]+):")]
    private static partial Regex PlaceRegex();

    [GeneratedRegex(@"Empty places: ([^\n]+)")]
    private static partial Regex EmptyPlacesRegex();

    [GeneratedRegex(@"Open vote (p\d+):")]
    private static partial Regex OpenVoteRegex();
}
