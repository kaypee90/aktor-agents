using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>Reads JSON or YAML into a JSON tree: OpenAPI documents, solution manifests.</summary>
public static class YamlJson
{
    /// <summary>A JSON or YAML document as JSON (YAML's plain scalars keep their type).</summary>
    public static JsonNode? Parse(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"The JSON couldn't be read: {ex.Message}");
            }
        }

        var parser = new YamlDotNet.Core.Parser(new StringReader(text));
        try
        {
            var yaml = new YamlStream();
            yaml.Load(parser);
            return yaml.Documents.Count == 0 ? null : FromYaml(yaml.Documents[0].RootNode, 0);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // The message doesn't say where; the dashboard highlights the line it names.
            throw new FormatException($"The YAML couldn't be read: (Line: {ex.Start.Line}, Col: {ex.Start.Column}): {ex.Message}");
        }
        catch (InvalidOperationException)
        {
            // YamlDotNet's scanner gives up this way on some broken input (an unclosed [ or {),
            // without saying where; the last thing it read is near the problem.
            var at = parser.Current is { } current ? $"(Line: {current.Start.Line}, Col: {current.Start.Column}): " : string.Empty;
            throw new FormatException($"The YAML couldn't be read: {at}a value there couldn't be read. Look for an unclosed [ or {{, or a stray quote.");
        }
    }

    private static JsonNode? FromYaml(YamlNode node, int depth)
    {
        if (depth > 64) throw new FormatException("The document is nested too deeply.");
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (key, value) in map.Children)
                {
                    if (key is YamlScalarNode { Value: { } k }) obj[k] = FromYaml(value, depth + 1);
                }

                return obj;
            case YamlSequenceNode seq:
                return new JsonArray(seq.Children.Select(c => FromYaml(c, depth + 1)).ToArray());
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                return null;
        }
    }

    /// <summary>Plain YAML scalars keep their type (true, 3, 2.5, null); quoted ones stay strings.</summary>
    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null) return null;
        if (scalar.Style is not YamlDotNet.Core.ScalarStyle.Plain) return JsonValue.Create(value);
        switch (value)
        {
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
            case "null" or "Null" or "NULL" or "~" or "": return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && value.Any(char.IsDigit)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }
}
