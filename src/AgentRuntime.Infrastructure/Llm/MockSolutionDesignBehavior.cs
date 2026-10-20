using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.LLM;
using AgentRuntime.Solutions;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// The Mock provider's stand-in for the solution designer, so the builder's conversation can be
/// tried with no API key. It understands a few phrasings ("add a Vendor entity with name, phone and
/// rate", "remove the Vendor entity", "add an eval", "rename it to …") and otherwise files the
/// request as a knowledge note, saying a real model is needed for more. It doesn't really design.
/// </summary>
internal static partial class MockSolutionDesignBehavior
{
    public static bool IsDesignRequest(LlmCompletionRequest request) =>
        request.Tools.Any(t => t.Name == SolutionDesignPrompt.ToolName);

    public static LlmCompletionResponse Design(LlmCompletionRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var wants = WantsRegex().Match(prompt).Groups[1].Value.Trim();
        var draftLine = prompt.Split('\n').Skip(1).FirstOrDefault() ?? "{}";
        JsonObject draft;
        try
        {
            draft = JsonNode.Parse(draftLine) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            draft = [];
        }

        string reply;
        object[] changes;
        if (AddEntityRegex().Match(wants) is { Success: true } add)
        {
            var name = Pascal(add.Groups[1].Value);
            var fields = new JsonObject();
            // "…and hourly rate, and let triage assign vendors": the field list ends where the sentence moves on.
            var list = Regex.Replace(add.Groups[2].Success ? add.Groups[2].Value : "name", @",?\s+and\s+(?:let|make|have|then|so|use)\b.*$", "", RegexOptions.IgnoreCase);
            foreach (var f in list.Split([",", " and "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var field = Snake(f);
                if (field.Length == 0) continue;
                fields[field] = new JsonObject { ["type"] = TypeFor(field), ["required"] = fields.Count == 0 };
            }

            reply = $"I've added a {name} entity with {fields.Count} field(s). (Mock LLM: connect a real model for full designs.)";
            changes = [new { op = "set", section = "entity", key = name, value = new JsonObject { ["description"] = $"A {name.ToLowerInvariant()}", ["fields"] = fields } }];
        }
        else if (RemoveEntityRegex().Match(wants) is { Success: true } remove)
        {
            var name = Pascal(remove.Groups[1].Value);
            reply = $"I've removed the {name} entity. Anything that still refers to it will show up as a problem to fix.";
            changes = [new { op = "remove", section = "entity", key = name }];
        }
        else if (RenameRegex().Match(wants) is { Success: true } rename)
        {
            var title = rename.Groups[1].Value.Trim().Trim('"', '\'', '.');
            reply = $"Renamed the solution to {title}.";
            changes = [new { op = "set", section = "basics", value = new { title } }];
        }
        else if (wants.Contains("eval", StringComparison.OrdinalIgnoreCase) && draft["workspaces"] is JsonArray { Count: > 0 } workspaces)
        {
            var key = workspaces[0]?["key"]?.GetValue<string>() ?? "main";
            reply = $"I've added an eval that runs the '{key}' workspace and expects it to complete.";
            changes = [new { op = "set", section = "eval", key = $"{key} completes", value = new { workspace = key, input = "A typical request.", expect = new { status = "Completed" } } }];
        }
        else
        {
            reply = "The Mock LLM can only add or remove entities, rename the solution, or add an eval. I've saved what you asked as a knowledge note; connect a real model to design the rest.";
            changes = [new { op = "set", section = "knowledge", key = "Design notes", value = new { text = wants } }];
        }

        return MockPipelineBehavior.Respond(MockPipelineBehavior.Call(SolutionDesignPrompt.ToolName, new { reply, summary = reply.Split('.')[0], changes }));
    }

    private static string TypeFor(string field) => field switch
    {
        _ when field.EndsWith("_on") || field.EndsWith("date") => FieldTypes.Date,
        _ when field.EndsWith("_at") => FieldTypes.DateTime,
        _ when Regex.IsMatch(field, "(price|amount|rent|cost|rate|total|fee)") => FieldTypes.Number,
        _ when Regex.IsMatch(field, "(count|quantity|qty)") => FieldTypes.Integer,
        _ when Regex.IsMatch(field, "(notes|details|description)") => FieldTypes.Text,
        _ => FieldTypes.String
    };

    private static string Pascal(string s) =>
        string.Concat(Regex.Split(s.Trim(), "[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Snake(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    [GeneratedRegex(@"What the person wants: (.+)")]
    private static partial Regex WantsRegex();

    [GeneratedRegex(@"\b(?:add|create)\s+(?:an?\s+)?([A-Za-z][\w ]*?)\s+entity(?:\s+with\s+(?:fields?\s+|a\s+|an\s+)?(.+?))?\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddEntityRegex();

    [GeneratedRegex(@"\b(?:remove|delete|drop)\s+(?:the\s+)?([A-Za-z]\w*)\s+entity", RegexOptions.IgnoreCase)]
    private static partial Regex RemoveEntityRegex();

    [GeneratedRegex(@"\b(?:rename|call)\s+(?:it|the solution)\s+(?:to\s+)?(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RenameRegex();
}
