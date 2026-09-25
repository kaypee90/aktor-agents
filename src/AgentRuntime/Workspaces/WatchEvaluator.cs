using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentRuntime.Workspaces;

/// <summary>One comparison in a watch's condition (all of a watch's conditions must hold).</summary>
[GenerateSerializer]
public sealed record WatchCondition
{
    /// <summary>Path within an item, e.g. "status" or "lines[0].amount".</summary>
    [Id(0)] public required string Field { get; init; }
    /// <summary>&lt; &lt;= &gt; &gt;= == != contains not_contains exists not_exists</summary>
    [Id(1)] public required string Op { get; init; }
    [Id(2)] public string? Value { get; init; }

    public override string ToString() => Op is "exists" or "not_exists" ? $"{Field} {Op}" : $"{Field} {Op} {Value}";
}

/// <summary>What a watch evaluates: where the items are in a tool result and what makes one match.</summary>
[GenerateSerializer]
public sealed record WatchRule
{
    /// <summary>JSONPath subset selecting the items: $, .name, ['name'], [n], [*]. Empty: the whole
    /// result is one item. JSON held in strings (an API body, MCP text) is parsed on the way.</summary>
    [Id(0)] public string ItemsPath { get; init; } = string.Empty;
    [Id(1)] public List<WatchCondition> Conditions { get; init; } = [];
    /// <summary>Identifies an item across checks, so it's reported when it starts matching, not every time.</summary>
    [Id(2)] public string? KeyField { get; init; }
    /// <summary>Fields shown for each matching item in the alert.</summary>
    [Id(3)] public List<string> DisplayFields { get; init; } = [];
}

public sealed record WatchMatch(string Key, string Summary, JsonNode? Item);

public sealed record WatchEvaluation(int ItemCount, IReadOnlyList<WatchMatch> Matches);

/// <summary>
/// Evaluates watch rules in plain code: no LLM, no scripting engine, nothing but path lookups and
/// comparisons. This is what lets a recurring check cost zero tokens until it finds something.
/// </summary>
public static class WatchEvaluator
{
    private static readonly HashSet<string> Ops = ["<", "<=", ">", ">=", "==", "!=", "contains", "not_contains", "exists", "not_exists"];

    public static string? Validate(WatchRule rule)
    {
        if (rule.Conditions.Count == 0) return "A watch needs at least one condition.";
        if (rule.Conditions.Count > 10) return "A watch can have at most 10 conditions.";
        foreach (var c in rule.Conditions)
        {
            if (string.IsNullOrWhiteSpace(c.Field)) return "Every condition needs a field.";
            if (!Ops.Contains(c.Op)) return $"Unknown operator '{c.Op}'. Use one of: {string.Join(" ", Ops)}.";
            if (c.Op is not ("exists" or "not_exists") && c.Value is null) return $"Condition on '{c.Field}' needs a value.";
            if (TryParsePath(c.Field, out _) is { } e) return $"Bad field '{c.Field}': {e}";
        }

        if (!string.IsNullOrWhiteSpace(rule.ItemsPath) && TryParsePath(rule.ItemsPath, out _) is { } pathError) return $"Bad items_path: {pathError}";
        return null;
    }

    public static WatchEvaluation Evaluate(string resultJson, WatchRule rule)
    {
        var root = ParseLenient(resultJson);
        var items = string.IsNullOrWhiteSpace(rule.ItemsPath) ? [root] : Select(root, rule.ItemsPath).ToList();
        var matches = new List<WatchMatch>();
        foreach (var item in items)
        {
            if (!rule.Conditions.All(c => Holds(item, c))) continue;
            var key = rule.KeyField is { Length: > 0 } kf && Select(item, kf).FirstOrDefault() is { } k
                ? Scalar(k) ?? k.ToJsonString()
                : Hash(item?.ToJsonString() ?? "null");
            matches.Add(new WatchMatch(key, Summarize(item, rule.DisplayFields, key), item));
        }

        return new WatchEvaluation(items.Count, matches);
    }

    private static bool Holds(JsonNode? item, WatchCondition c)
    {
        var values = Select(item, c.Field).ToList();
        return c.Op switch
        {
            "exists" => values.Any(v => v is not null),
            "not_exists" => values.All(v => v is null),
            _ => values.Any(v => Compare(v, c.Op, c.Value!))
        };
    }

    private static bool Compare(JsonNode? node, string op, string expected)
    {
        if (node is null) return op == "!=";

        if (op is "contains" or "not_contains")
        {
            var has = node is JsonArray arr
                ? arr.Any(e => string.Equals(Scalar(e), expected, StringComparison.OrdinalIgnoreCase))
                : (Scalar(node) ?? node.ToJsonString()).Contains(expected, StringComparison.OrdinalIgnoreCase);
            return op == "contains" ? has : !has;
        }

        var actual = Scalar(node);
        if (actual is null) return false;

        if (double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            return op switch
            {
                "<" => a < b,
                "<=" => a <= b,
                ">" => a > b,
                ">=" => a >= b,
                "==" => a == b,
                "!=" => a != b,
                _ => false
            };
        }

        var cmp = string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            _ => false
        };
    }

    // ---- JSONPath subset ------------------------------------------------------

    private abstract record Step;
    private sealed record Prop(string Name) : Step;
    private sealed record Index(int I) : Step;
    private sealed record Wildcard : Step;

    public static IEnumerable<JsonNode?> Select(JsonNode? root, string path)
    {
        if (TryParsePath(path, out var steps) is { } error) throw new FormatException(error);

        IEnumerable<JsonNode?> current = [root];
        foreach (var step in steps)
        {
            current = current.SelectMany(n => Apply(Unwrap(n), step)).ToList();
        }

        return current.Select(Unwrap);
    }

    private static IEnumerable<JsonNode?> Apply(JsonNode? node, Step step) => (node, step) switch
    {
        (JsonObject o, Prop p) => o.TryGetPropertyValue(p.Name, out var v) ? [v] : [],
        (JsonArray a, Index i) => i.I >= 0 && i.I < a.Count ? [a[i.I]] : i.I < 0 && -i.I <= a.Count ? [a[a.Count + i.I]] : [],
        (JsonArray a, Wildcard) => a,
        (JsonObject o, Wildcard) => o.Select(kv => kv.Value),
        _ => []
    };

    /// <summary>JSON inside a string (an HTTP body, MCP text content) is parsed transparently.</summary>
    private static JsonNode? Unwrap(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            var t = s.TrimStart();
            if (t.StartsWith('{') || t.StartsWith('['))
            {
                try { return JsonNode.Parse(s); }
                catch (JsonException) { return node; }
            }
        }

        return node;
    }

    private static string? TryParsePath(string path, out List<Step> steps)
    {
        steps = [];
        var p = path.Trim();
        var i = 0;
        if (p.StartsWith('$')) i = 1;

        while (i < p.Length)
        {
            if (p[i] == '.')
            {
                i++;
                var start = i;
                while (i < p.Length && p[i] is not ('.' or '[')) i++;
                var name = p[start..i];
                if (name.Length == 0) return "empty property name";
                steps.Add(name == "*" ? new Wildcard() : new Prop(name));
            }
            else if (p[i] == '[')
            {
                var end = p.IndexOf(']', i);
                if (end < 0) return "missing ']'";
                var inner = p[(i + 1)..end].Trim();
                i = end + 1;
                if (inner == "*") steps.Add(new Wildcard());
                else if (inner.Length >= 2 && inner[0] is '\'' or '"' && inner[^1] == inner[0]) steps.Add(new Prop(inner[1..^1]));
                else if (int.TryParse(inner, out var idx)) steps.Add(new Index(idx));
                else return $"unsupported selector [{inner}]";
            }
            else
            {
                // A bare leading name ("status", "lines[0].amount").
                var start = i;
                while (i < p.Length && p[i] is not ('.' or '[')) i++;
                steps.Add(new Prop(p[start..i]));
            }
        }

        return null;
    }

    // ---- Helpers --------------------------------------------------------------

    private static JsonNode? ParseLenient(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(json); }
    }

    private static string? Scalar(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null
    };

    private static string Summarize(JsonNode? item, List<string> fields, string key)
    {
        if (fields.Count == 0) return key;
        var parts = fields.Select(f => $"{f}={Scalar(Select(item, f).FirstOrDefault()) ?? "?"}");
        return string.Join(", ", parts);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
