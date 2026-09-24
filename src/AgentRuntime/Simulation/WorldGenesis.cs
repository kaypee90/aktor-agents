using System.Text.Json;
using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Simulation;

/// <summary>Turns a short human description of a world into a concrete blueprint: locations and
/// the initial residents with personas and drives.</summary>
public interface IWorldGenesis
{
    Task<WorldBlueprint> GenerateAsync(string seed, int population, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the LLM to call a single <c>define_world</c> tool, so the blueprint arrives as structured
/// arguments rather than prose that would have to be parsed (CLAUDE.md section 16).
/// </summary>
public sealed class LlmWorldGenesis(ILLMProvider llm, IOptions<LlmOptions> llmOptions) : IWorldGenesis
{
    public const string ToolName = "define_world";

    private static readonly LlmToolDefinition DefineWorldTool = new()
    {
        Name = ToolName,
        Description = "Define the simulated world: its name, a short description, its locations, and its initial residents.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "world_name": { "type": "string" },
            "world_description": { "type": "string", "description": "2-4 sentences: the setting, its situation, and its tensions" },
            "locations": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "name": { "type": "string" }, "description": { "type": "string" } },
                "required": ["name", "description"]
              }
            },
            "residents": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "role": { "type": "string", "description": "Short identity, e.g. 'baker', 'investor'" },
                  "persona": { "type": "string", "description": "2-3 sentences of personality, background and quirks" },
                  "drives": { "type": "string", "description": "What they want, in one or two sentences" },
                  "starting_location": { "type": "string" },
                  "relationships": { "type": "string", "description": "How they relate to specific other residents" }
                },
                "required": ["name", "role", "persona", "drives", "starting_location"]
              }
            }
          },
          "required": ["world_name", "world_description", "locations", "residents"]
        }
        """
    };

    public async Task<WorldBlueprint> GenerateAsync(string seed, int population, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You design small simulated worlds for autonomous AI agents to live in. Create a vivid, " +
                "coherent setting with 3-6 locations and residents whose drives overlap and conflict, so " +
                "cooperation, rivalry and negotiation can emerge on their own. Give each resident a distinct " +
                "voice. Respond ONLY by calling the define_world tool."),
            ChatMessage.User($"Create a world with exactly {population} residents based on this description:\n\n{seed}")
        };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await llm.CompleteAsync(new LlmCompletionRequest
            {
                Messages = messages,
                Tools = [DefineWorldTool],
                Model = llmOptions.Value.Model,
                Temperature = 0.9,
                MaxTokens = 4096
            }, cancellationToken);

            var call = response.ToolCalls.FirstOrDefault(c => c.Name == ToolName);
            if (call is not null && TryParse(call.ArgumentsJson, out var blueprint))
            {
                return blueprint;
            }

            messages.Add(ChatMessage.User("You must call the define_world tool with the complete world definition."));
        }

        throw new InvalidOperationException("The LLM did not produce a valid world definition. Try again or rephrase the description.");
    }

    public static bool TryParse(string json, out WorldBlueprint blueprint)
    {
        blueprint = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var locations = Array(root, "locations")
                .Select(l => new LocationBlueprint { Name = Str(l, "name"), Description = Str(l, "description") })
                .Where(l => l.Name.Length > 0)
                .ToList();
            var residents = Array(root, "residents")
                .Select(r => new ResidentBlueprint
                {
                    Name = Str(r, "name"),
                    Role = Str(r, "role"),
                    Persona = Str(r, "persona"),
                    Drives = Str(r, "drives"),
                    StartingLocation = Str(r, "starting_location"),
                    Relationships = Str(r, "relationships")
                })
                .Where(r => r.Name.Length > 0)
                .ToList();

            if (residents.Count == 0) return false;

            blueprint = new WorldBlueprint
            {
                Name = Str(root, "world_name"),
                Description = Str(root, "world_description"),
                Locations = locations,
                Residents = residents
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim()
            : string.Empty;
}
