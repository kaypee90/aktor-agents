using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRuntime.Simulation;

/// <summary>
/// The actions a resident can take in its world, exposed as structured tools (CLAUDE.md section
/// 16). Each tool only translates its arguments into a <see cref="WorldAction"/>; the world grain
/// validates it, charges energy and applies it, so nothing here is trusted to enforce rules.
/// </summary>
public sealed record WorldToolSpec(
    string Name,
    string Kind,
    string Description,
    string JsonSchema,
    ToolPermission Permissions,
    Func<JsonElement, WorldAction> Parse);

public sealed class WorldTool(IGrainFactory grainFactory, WorldToolSpec spec) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = spec.Name,
        Description = spec.Description,
        RequiredPermissions = spec.Permissions,
        // The world grain deduplicates by idempotency key, so replaying any world action is safe.
        SideEffects = spec.Kind == "look" ? ToolSideEffects.ReadOnly : ToolSideEffects.Idempotent,
        JsonSchema = spec.JsonSchema
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        // A resident's task id is its world id; anything else isn't a resident.
        if (!WorldIds.IsWorld(request.TaskId))
        {
            return ToolExecutionResult.Fail($"'{spec.Name}' only works inside a simulated world.");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
        var result = await grainFactory.GetGrain<IWorldGrain>(request.TaskId).Act(request.AgentId, spec.Parse(doc.RootElement), request.IdempotencyKey);

        return result.Success
            ? ToolExecutionResult.Ok(result.ResultJson ?? JsonSerializer.Serialize(new { ok = true, message = result.Message }, ToolJson.Options))
            : ToolExecutionResult.Fail(result.Message);
    }
}

public static class WorldToolCatalog
{
    private const ToolPermission World = ToolPermission.WorldActions;

    private const string TextSchema = """{ "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"] }""";

    public static readonly IReadOnlyList<WorldToolSpec> Specs =
    [
        new("look_around", "look",
            "See the whole world: every location and who is there, all residents, recent board posts, and open votes. Free.",
            """{ "type": "object", "properties": {} }""", World, _ => new WorldAction { Kind = "look" }),

        new("move_to", "move",
            "Walk to another location. Residents there will see you arrive.",
            """{ "type": "object", "properties": { "location": { "type": "string" } }, "required": ["location"] }""",
            World, a => new WorldAction { Kind = "move", Location = Str(a, "location") }),

        new("say", "say",
            "Speak out loud. Everyone at your current location hears it on their next turn.",
            TextSchema, World, a => new WorldAction { Kind = "say", Text = Str(a, "text") }),

        new("talk_to", "talk",
            "Send a private message to one resident anywhere in the world. They are woken to read it right away.",
            """{ "type": "object", "properties": { "agent_id": { "type": "string" }, "text": { "type": "string" } }, "required": ["agent_id", "text"] }""",
            World | ToolPermission.SendMessages, a => new WorldAction { Kind = "talk", TargetId = Str(a, "agent_id"), Text = Str(a, "text") }),

        new("post_to_board", "post",
            "Post a public notice on the world board. Every resident sees it.",
            TextSchema, World, a => new WorldAction { Kind = "post", Text = Str(a, "text") }),

        new("give_energy", "give",
            "Give some of your energy to another resident. Giving energy to a dormant resident revives them.",
            """{ "type": "object", "properties": { "agent_id": { "type": "string" }, "amount": { "type": "integer" } }, "required": ["agent_id", "amount"] }""",
            World, a => new WorldAction { Kind = "give", TargetId = Str(a, "agent_id"), Amount = Int(a, "amount") }),

        new("propose_removal", "propose_removal",
            "Propose a vote to remove a resident from the world. It passes only if a majority of residents vote for it.",
            """{ "type": "object", "properties": { "agent_id": { "type": "string" }, "reason": { "type": "string" } }, "required": ["agent_id", "reason"] }""",
            World, a => new WorldAction { Kind = "propose_removal", TargetId = Str(a, "agent_id"), Text = Str(a, "reason") }),

        new("vote", "vote",
            "Vote on an open removal proposal. support=true means you want the resident removed.",
            """{ "type": "object", "properties": { "proposal_id": { "type": "string" }, "support": { "type": "boolean" } }, "required": ["proposal_id", "support"] }""",
            World, a => new WorldAction { Kind = "vote", ProposalId = Str(a, "proposal_id"), Support = Bool(a, "support") }),

        new("bring_new_agent", "bring",
            "Bring a new resident into the world (a hire, a child, a recruit...). Expensive in energy; they appear at your location.",
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "role": { "type": "string", "description": "Short identity, e.g. 'apprentice baker'" },
                "persona": { "type": "string", "description": "Their personality and background" },
                "drives": { "type": "string", "description": "What they want in life" }
              },
              "required": ["name", "role", "persona", "drives"]
            }
            """,
            World | ToolPermission.SpawnAgents,
            a => new WorldAction { Kind = "bring", Name = Str(a, "name"), Role = Str(a, "role"), Persona = Str(a, "persona"), Drives = Str(a, "drives") }),

        new("note_to_self", "note",
            "Write a private note to remember something. Your recent notes are shown to you every turn. Free.",
            TextSchema, World, a => new WorldAction { Kind = "note", Text = Str(a, "text") }),

        new("leave_world", "leave",
            "Leave the world permanently. You stop existing in it. Free.",
            """{ "type": "object", "properties": { "farewell": { "type": "string" } } }""",
            World, a => new WorldAction { Kind = "leave", Text = Str(a, "farewell") }),

        new("end_turn", "end_turn",
            "End your turn for now. Give a one-line plan for what you intend next (visible only to you and the observers). Free.",
            """{ "type": "object", "properties": { "plan": { "type": "string" } }, "required": ["plan"] }""",
            World, a => new WorldAction { Kind = "end_turn", Text = Str(a, "plan") })
    ];

    public static readonly IReadOnlyList<string> ToolNames = Specs.Select(s => s.Name).ToList();

    public static IServiceCollection AddWorldTools(this IServiceCollection services)
    {
        foreach (var spec in Specs)
        {
            services.AddSingleton<ITool>(sp => new WorldTool(sp.GetRequiredService<IGrainFactory>(), spec));
        }

        return services;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    private static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return int.TryParse(v.ToString(), out n) ? n : 0;
    }

    private static bool Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(v.ToString(), out var b) && b
        };
    }
}
