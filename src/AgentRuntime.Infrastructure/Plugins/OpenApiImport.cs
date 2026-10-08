using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>What an OpenAPI document offers an HTTP API connection: its endpoints and base URL.</summary>
public sealed record OpenApiImportResult
{
    public string? Title { get; init; }
    public string? BaseUrl { get; init; }
    public List<ApiEndpoint> Endpoints { get; init; } = [];
    /// <summary>What was skipped or simplified (header parameters, oversized schemas, extra operations).</summary>
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Turns an OpenAPI 3.x or Swagger 2.0 document (JSON or YAML) into <see cref="ApiEndpoint"/>s.
/// Pure: it never fetches anything, so a spec can't make the server call out. $refs within the
/// document are inlined (cycles and deep nesting cut off); header and cookie parameters are
/// skipped because the connection owns its headers.
/// </summary>
public static class OpenApiImport
{
    public const int MaxSpecChars = 5_000_000;
    public const int MaxOperations = 300;
    private const int MaxRefDepth = 6;
    private static readonly string[] Verbs = ["get", "post", "put", "patch", "delete"];
    private static readonly string[] DroppedSchemaKeys = ["example", "examples", "xml", "externalDocs", "discriminator", "deprecated", "readOnly", "writeOnly"];

    public static OpenApiImportResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("The document is empty.");
        if (text.Length > MaxSpecChars) throw new FormatException("The document is too large (over 5 MB).");

        var root = (Load(text) as JsonObject) ?? throw new FormatException("The document isn't an OpenAPI object.");
        // Versions may be YAML numbers (openapi: 3.0), so read them as text.
        var swagger2 = root["swagger"]?.ToString().StartsWith('2') == true;
        if (!swagger2 && root["openapi"] is null) throw new FormatException("This isn't an OpenAPI document (no 'openapi' or 'swagger' field).");
        if (root["paths"] is not JsonObject paths) throw new FormatException("The document has no paths.");

        var warnings = new List<string>();
        var endpoints = new List<ApiEndpoint>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var operations = 0;
        foreach (var (path, item) in paths)
        {
            if (item is not JsonObject pathItem) continue;
            var shared = pathItem["parameters"] as JsonArray;
            foreach (var verb in Verbs)
            {
                if (pathItem[verb] is not JsonObject op) continue;
                operations++;
                if (endpoints.Count >= MaxOperations) continue;
                endpoints.Add(Operation(root, path, verb, op, shared, names, swagger2, warnings));
            }
        }

        if (operations > MaxOperations) warnings.Add($"The document has {operations} operations; the first {MaxOperations} were read.");
        return new OpenApiImportResult
        {
            Title = Str(root["info"]?["title"]),
            BaseUrl = BaseUrl(root, swagger2, warnings),
            Endpoints = endpoints,
            Warnings = warnings
        };
    }

    private static ApiEndpoint Operation(JsonObject root, string path, string verb, JsonObject op, JsonArray? shared,
        HashSet<string> names, bool swagger2, List<string> warnings)
    {
        var name = Unique(ToolName(Str(op["operationId"]), verb, path), names);
        var parameters = new Dictionary<string, ApiEndpointParam>(StringComparer.Ordinal);
        JsonNode? body = null;
        var skippedHeaders = false;

        foreach (var raw in (shared ?? []).Concat(op["parameters"] as JsonArray ?? []))
        {
            if (Resolve(root, raw, 0, []) is not JsonObject p || Str(p["name"]) is not { } paramName) continue;
            switch (Str(p["in"]))
            {
                case "path" or "query":
                    var where = Str(p["in"])!;
                    parameters[$"{where}:{paramName}"] = new ApiEndpointParam
                    {
                        Name = paramName,
                        In = where,
                        Type = ParamType(Str(p["schema"]?["type"]) ?? Str(p["type"])),
                        Required = where == "path" || Bool(p["required"]),
                        Description = Clip(Str(p["description"]), 200)
                    };
                    break;
                case "body" when swagger2:
                    body = Schema(root, p["schema"]);
                    break;
                case "header" or "cookie":
                    skippedHeaders = true;
                    break;
            }
        }

        if (!swagger2 && Resolve(root, op["requestBody"], 0, []) is JsonObject requestBody)
        {
            var content = requestBody["content"] as JsonObject;
            var json = content?.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase)).Value;
            if (json is not null) body = Schema(root, json["schema"]);
            else if (content is { Count: > 0 }) warnings.Add($"{name}: its request body isn't JSON ({content.First().Key}); the tool sends JSON.");
        }

        if (skippedHeaders) warnings.Add($"{name}: header or cookie parameters were skipped (the connection sets its own headers).");
        if (body is not null && body.ToJsonString().Length > HttpApiEndpoints.MaxBodySchemaChars)
        {
            warnings.Add($"{name}: its body schema was too large and was replaced by a plain JSON object.");
            body = new JsonObject { ["type"] = "object", ["description"] = "JSON request body (see the API's documentation)." };
        }

        if (body is not null && body is not JsonObject) body = null;

        var summary = Str(op["summary"]);
        var description = Str(op["description"]);
        var about = summary is null ? description : description is null || description == summary ? summary : $"{summary}. {description}";
        return HttpApiEndpoints.Normalize(new ApiEndpoint
        {
            Name = name,
            Method = verb.ToUpperInvariant(),
            Path = path,
            Description = Clip(about, 300),
            Params = parameters.Values.ToList(),
            BodySchema = body
        });
    }

    private static string? BaseUrl(JsonObject root, bool swagger2, List<string> warnings)
    {
        if (swagger2)
        {
            var host = Str(root["host"]);
            if (host is null) return null;
            var scheme = (root["schemes"] as JsonArray)?.Select(Str).FirstOrDefault(s => s is "https") ?? "https";
            return $"{scheme}://{host}{Str(root["basePath"])?.TrimEnd('/')}";
        }

        if ((root["servers"] as JsonArray)?.FirstOrDefault() is not JsonObject server || Str(server["url"]) is not { } url) return null;
        // Server variables: their defaults ({region} → eu).
        if (server["variables"] is JsonObject variables)
        {
            foreach (var (key, value) in variables) url = url.Replace("{" + key + "}", Str(value?["default"]) ?? string.Empty, StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            warnings.Add($"The document's server URL '{url}' is relative: enter the full base URL yourself.");
            return null;
        }

        return url.TrimEnd('/');
    }

    /// <summary>A schema with its $refs inlined and documentation-only keys dropped.</summary>
    private static JsonNode? Schema(JsonObject root, JsonNode? schema) => Clean(Resolve(root, schema, 0, []));

    private static JsonNode? Resolve(JsonObject root, JsonNode? node, int depth, HashSet<string> visiting)
    {
        switch (node)
        {
            case JsonObject obj when Str(obj["$ref"]) is { } reference:
                if (depth >= MaxRefDepth || !visiting.Add(reference)) return new JsonObject { ["type"] = "object" };
                var target = Lookup(root, reference);
                var resolved = target is null ? new JsonObject { ["type"] = "object" } : Resolve(root, target, depth + 1, visiting);
                visiting.Remove(reference);
                return resolved;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj) copy[key] = Resolve(root, value, depth, visiting);
                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(v => Resolve(root, v, depth, visiting)).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? Lookup(JsonObject root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null; // other files are never fetched
        JsonNode? current = root;
        foreach (var part in reference[2..].Split('/'))
        {
            current = (current as JsonObject)?[Uri.UnescapeDataString(part).Replace("~1", "/").Replace("~0", "~")];
            if (current is null) return null;
        }

        return current;
    }

    private static JsonNode? Clean(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in DroppedSchemaKeys) obj.Remove(key);
            foreach (var key in obj.Select(kv => kv.Key).Where(k => k.StartsWith("x-", StringComparison.Ordinal)).ToList()) obj.Remove(key);
            foreach (var (_, value) in obj.ToList()) Clean(value);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) Clean(item);
        }

        return node;
    }

    /// <summary>getOrderById → get_order_by_id; without an operationId, GET /orders/{id} → get_orders_id.</summary>
    public static string ToolName(string? operationId, string verb, string path)
    {
        var source = operationId ?? $"{verb} {path.Replace("{", string.Empty).Replace("}", string.Empty)}";
        var sb = new StringBuilder();
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(source[i - 1]) || char.IsDigit(source[i - 1]))) sb.Append('_');
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }

        var name = string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = verb + "_" + name;
        if (name is "get" or "send") name += "_" + verb;
        return name.Length > 44 ? name[..44].TrimEnd('_') : name;
    }

    private static string Unique(string name, HashSet<string> names)
    {
        var candidate = name;
        for (var i = 2; !names.Add(candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }

    private static string ParamType(string? type) => type is "integer" or "number" or "boolean" ? type : "string";

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static bool Bool(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string? Clip(string? text, int max) =>
        text is null ? null : text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    // ---- JSON or YAML -------------------------------------------------------------

    private static JsonNode? Load(string text)
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

        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            return yaml.Documents.Count == 0 ? null : FromYaml(yaml.Documents[0].RootNode, 0);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new FormatException($"The YAML couldn't be read: {ex.Message}");
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
