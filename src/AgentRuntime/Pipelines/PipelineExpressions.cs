using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Pipelines;

/// <summary>
/// <c>{{ expression }}</c>s in an action stage's arguments, filled in from the run's data. An
/// expression is a path into the data a stage sees:
/// <list type="bullet">
/// <item><c>input</c>: the run's input (a form's answers, a webhook's JSON, or the text typed);</item>
/// <item><c>stages.&lt;id&gt;</c>: an earlier stage's output, e.g. <c>stages.read-sheet.rows[0].email</c>;</item>
/// <item><c>previous</c>: the output of the stage's only input;</item>
/// <item><c>item</c> and <c>index</c>: in a for-each, the current item and its position;</item>
/// <item><c>run</c>: the run's id and number.</item>
/// </list>
/// Paths use the same JSONPath subset as watches (<c>.name</c>, <c>['name']</c>, <c>[n]</c>, <c>[*]</c>).
/// A string that is exactly one expression becomes that value with its type (a number, a list, an
/// object); expressions inside longer text are written into it.
/// </summary>
public static partial class PipelineExpressions
{
    public static readonly string[] Roots = ["input", "stages", "previous", "item", "index", "run"];

    [GeneratedRegex(@"\{\{\s*(.*?)\s*\}\}", RegexOptions.Singleline)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\s*\{\{\s*(.*?)\s*\}\}\s*$", RegexOptions.Singleline)]
    private static partial Regex Whole();

    /// <summary>Why <paramref name="expression"/> can't be evaluated; null when it can.</summary>
    public static string? CheckExpression(string expression)
    {
        var e = expression.Trim();
        if (e.Length == 0) return "the expression is empty.";
        var root = RootOf(e);
        if (!Roots.Contains(root))
        {
            return $"'{{{{ {e} }}}}' doesn't say where the value comes from. Write {{{{ input.{e} }}}} for a value from the run's input " +
                   $"(a form field, a webhook's JSON), {{{{ previous.{e} }}}} for the step before, or {{{{ stages.<step-id>.{e} }}}} for another step. " +
                   $"Expressions start with one of: {string.Join(", ", Roots)}.";
        }
        try
        {
            _ = WatchEvaluator.Select(null, e).ToList();
            return null;
        }
        catch (FormatException ex)
        {
            return $"'{e}' isn't a valid path ({ex.Message}).";
        }
    }

    /// <summary>Why <paramref name="template"/> isn't a usable arguments template; null when it is.</summary>
    public static string? CheckTemplate(string template)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(template) ? "{}" : template);
        }
        catch (JsonException ex)
        {
            return $"the arguments aren't valid JSON ({ex.Message}).";
        }

        if (node is not JsonObject) return "the arguments must be a JSON object.";
        foreach (var text in Strings(node))
        {
            foreach (Match m in Placeholder().Matches(text))
            {
                if (CheckExpression(m.Groups[1].Value) is { } problem) return problem;
            }
        }

        return null;
    }

    /// <summary>The value of <paramref name="expression"/> in <paramref name="context"/>: null when
    /// nothing is there, a list when the path has a wildcard.</summary>
    public static JsonNode? Evaluate(string expression, JsonObject context)
    {
        var e = expression.Trim();
        var found = WatchEvaluator.Select(context, e).Select(n => n?.DeepClone()).ToList();
        return e.Contains("[*]", StringComparison.Ordinal) ? new JsonArray([.. found]) : found.FirstOrDefault();
    }

    /// <summary>The arguments with every expression filled in.</summary>
    public static JsonObject Render(string template, JsonObject context)
    {
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(template) ? "{}" : template) as JsonObject
                   ?? throw new FormatException("The arguments must be a JSON object.");
        return (JsonObject)Fill(node, context)!;
    }

    private static JsonNode? Fill(JsonNode? node, JsonObject context)
    {
        switch (node)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (key, value) in o) obj[key] = Fill(value, context);
                return obj;
            case JsonArray a:
                return new JsonArray([.. a.Select(v => Fill(v, context))]);
            case JsonValue v when v.TryGetValue<string>(out var text):
                if (Whole().Match(text) is { Success: true } whole) return Evaluate(whole.Groups[1].Value, context);
                return Placeholder().IsMatch(text)
                    ? JsonValue.Create(Placeholder().Replace(text, m => AsText(Evaluate(m.Groups[1].Value, context))))
                    : JsonValue.Create(text);
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>How a value reads inside text: strings as they are, anything else as JSON.</summary>
    public static string AsText(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString()
    };

    private static string RootOf(string expression)
    {
        var end = expression.IndexOfAny(['.', '[']);
        return (end < 0 ? expression : expression[..end]).TrimStart('$');
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(kv => Strings(kv.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => []
    };
}
