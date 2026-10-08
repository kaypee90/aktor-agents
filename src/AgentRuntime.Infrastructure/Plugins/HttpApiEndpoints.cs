using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>One parameter of an <see cref="ApiEndpoint"/>: a placeholder in its path, or a query
/// string value. Headers aren't offered: the connection owns them (its credential above all).</summary>
public sealed record ApiEndpointParam
{
    public string Name { get; init; } = string.Empty;
    /// <summary>"path" or "query".</summary>
    public string In { get; init; } = "query";
    /// <summary>string, integer, number or boolean.</summary>
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// One operation of an HTTP API connection, added by hand or imported from an OpenAPI document
/// (docs/plugins.md, "Endpoints"). Each becomes a typed tool: agents (and the connection's MCP
/// gateway) fill in its parameters, and the runtime builds the request under the base URL.
/// </summary>
public sealed record ApiEndpoint
{
    /// <summary>The tool name: lowercase letters, digits and underscores, e.g. get_order.</summary>
    public string Name { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    /// <summary>Relative to the base URL, with {placeholders}: /orders/{order_id}.</summary>
    public string Path { get; init; } = "/";
    public string? Description { get; init; }
    public List<ApiEndpointParam> Params { get; init; } = [];
    /// <summary>JSON schema of the request body (POST, PUT, PATCH); null for any JSON object.</summary>
    public JsonNode? BodySchema { get; init; }
}

public static partial class HttpApiEndpoints
{
    /// <summary>The connection setting that holds the endpoints, as a JSON array.</summary>
    public const string SettingKey = "endpoints";
    public const int MaxEndpoints = 100;
    public const int MaxBodySchemaChars = 8000;

    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly string[] Reserved = ["get", "send"];
    private static readonly string[] ParamTypes = ["string", "integer", "number", "boolean"];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\{([^{}/]+)\}")]
    private static partial Regex Placeholder();

    public static bool IsWrite(string method) => !method.Equals("GET", StringComparison.OrdinalIgnoreCase);

    public static ToolSideEffects SideEffectsOf(string method) => method.ToUpperInvariant() switch
    {
        "GET" => ToolSideEffects.ReadOnly,
        "PUT" or "DELETE" => ToolSideEffects.Idempotent,
        _ => ToolSideEffects.NonIdempotent
    };

    /// <summary>The placeholders of a path, in order: /orders/{id}/lines/{line} → id, line.</summary>
    public static IReadOnlyList<string> PathParams(string path) =>
        Placeholder().Matches(path).Select(m => m.Groups[1].Value.Trim()).ToList();

    /// <summary>Reads the connection's endpoints. An empty or missing setting is no endpoints; anything
    /// else must be valid, or the connection is refused with every problem listed.</summary>
    public static IReadOnlyList<ApiEndpoint> Parse(string? json, out List<string> errors)
    {
        errors = [];
        if (string.IsNullOrWhiteSpace(json)) return [];

        List<ApiEndpoint>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ApiEndpoint>>(json, Json);
        }
        catch (JsonException ex)
        {
            errors.Add($"Endpoints aren't valid JSON: {ex.Message}");
            return [];
        }

        var endpoints = (parsed ?? []).Select(Normalize).ToList();
        errors.AddRange(Validate(endpoints));
        return errors.Count == 0 ? endpoints : [];
    }

    public static string Serialize(IEnumerable<ApiEndpoint> endpoints) => JsonSerializer.Serialize(endpoints, Json);

    /// <summary>Fills in what can be inferred: upper-case method, a leading slash, and a required
    /// string parameter for every path placeholder that wasn't declared.</summary>
    public static ApiEndpoint Normalize(ApiEndpoint e)
    {
        var path = string.IsNullOrWhiteSpace(e.Path) ? "/" : "/" + e.Path.Trim().TrimStart('/');
        var declared = e.Params
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p with
            {
                Name = p.Name.Trim(),
                In = (p.In ?? "query").Trim().ToLowerInvariant(),
                Type = string.IsNullOrWhiteSpace(p.Type) ? "string" : p.Type.Trim().ToLowerInvariant()
            })
            .ToList();
        foreach (var name in PathParams(path))
        {
            if (!declared.Any(p => p.In == "path" && p.Name == name))
            {
                declared.Insert(0, new ApiEndpointParam { Name = name, In = "path", Required = true });
            }
        }

        return e with
        {
            Name = (e.Name ?? string.Empty).Trim(),
            Method = (e.Method ?? "GET").Trim().ToUpperInvariant(),
            Path = path,
            Description = string.IsNullOrWhiteSpace(e.Description) ? null : e.Description.Trim(),
            // A path placeholder is always required: the URL can't be built without it.
            Params = declared.Select(p => p.In == "path" ? p with { Required = true } : p).ToList()
        };
    }

    public static List<string> Validate(IReadOnlyList<ApiEndpoint> endpoints)
    {
        var errors = new List<string>();
        if (endpoints.Count > MaxEndpoints) errors.Add($"At most {MaxEndpoints} endpoints per connection ({endpoints.Count} given).");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in endpoints)
        {
            var label = string.IsNullOrEmpty(e.Name) ? $"{e.Method} {e.Path}" : e.Name;
            if (!NamePattern().IsMatch(e.Name)) errors.Add($"'{label}': the name must start with a letter and use only lowercase letters, digits and underscores (at most 48).");
            else if (Reserved.Contains(e.Name)) errors.Add($"'{e.Name}' is reserved for the connection's general tools; pick another name.");
            else if (!names.Add(e.Name)) errors.Add($"Two endpoints are named '{e.Name}'.");

            if (!Methods.Contains(e.Method)) errors.Add($"'{label}': the method must be one of {string.Join(", ", Methods)}.");
            if (e.Path.Contains("..") || e.Path.Contains('\\') || e.Path.Contains("://") || e.Path.StartsWith("//"))
            {
                errors.Add($"'{label}': the path must be relative to the base URL, without '..'.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.Params)
            {
                if (p.In is not ("path" or "query")) errors.Add($"'{label}': parameter '{p.Name}' must be in the path or the query.");
                if (!ParamTypes.Contains(p.Type)) errors.Add($"'{label}': parameter '{p.Name}' has type '{p.Type}'; use string, integer, number or boolean.");
                if (p.Name == "body") errors.Add($"'{label}': 'body' is the request body; name the parameter something else.");
                if (!seen.Add(p.Name)) errors.Add($"'{label}': parameter '{p.Name}' is declared twice.");
                if (p.In == "path" && !PathParams(e.Path).Contains(p.Name)) errors.Add($"'{label}': path parameter '{p.Name}' isn't in the path.");
            }

            if (e.BodySchema is not null)
            {
                if (e.BodySchema is not JsonObject) errors.Add($"'{label}': the body schema must be a JSON object.");
                else if (e.BodySchema.ToJsonString().Length > MaxBodySchemaChars) errors.Add($"'{label}': the body schema is longer than {MaxBodySchemaChars} characters.");
            }
        }

        return errors;
    }

    public static bool HasBody(ApiEndpoint e) => e.Method is "POST" or "PUT" or "PATCH" || e.BodySchema is not null && e.Method != "GET";

    /// <summary>The tool's input schema: one property per parameter, plus "body" for writes.</summary>
    public static string SchemaOf(ApiEndpoint e)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in e.Params)
        {
            var prop = new JsonObject { ["type"] = p.Type };
            var about = p.In == "path" ? "In the path." : "Query string.";
            prop["description"] = string.IsNullOrWhiteSpace(p.Description) ? about : $"{p.Description} ({about})";
            properties[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }

        if (HasBody(e))
        {
            properties["body"] = e.BodySchema?.DeepClone() ?? new JsonObject { ["type"] = "object", ["description"] = "JSON request body." };
            if (e.BodySchema is not null && e.Method is "POST" or "PUT" or "PATCH") required.Add("body");
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema.ToJsonString();
    }

    public static string DescriptionOf(ApiEndpoint e, string about) =>
        $"{e.Method} {e.Path} on {about}." + (string.IsNullOrWhiteSpace(e.Description) ? string.Empty : " " + e.Description) +
        (IsWrite(e.Method) ? " Changes real data." : string.Empty);

    /// <summary>Builds the relative path and query from a call's arguments. Path values are escaped
    /// and can't contain separators, so a value can't move the request to another path.</summary>
    public static bool TryBuild(ApiEndpoint e, JsonElement args, out string path, out JsonObject query, out string error)
    {
        path = e.Path;
        query = [];
        error = string.Empty;
        foreach (var p in e.Params)
        {
            var value = default(JsonElement);
            var present = args.ValueKind == JsonValueKind.Object && args.TryGetProperty(p.Name, out value) && value.ValueKind != JsonValueKind.Null;
            if (!present)
            {
                if (p.Required)
                {
                    error = $"'{p.Name}' is required.";
                    return false;
                }

                continue;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (p.In == "path")
            {
                if (text.Length == 0 || text is "." or ".." || text.Contains('/') || text.Contains('\\'))
                {
                    error = $"'{p.Name}' must be a single path segment (no '/', '.' or '..').";
                    return false;
                }

                path = path.Replace("{" + p.Name + "}", Uri.EscapeDataString(text), StringComparison.Ordinal);
            }
            else
            {
                query[p.Name] = text;
            }
        }

        return true;
    }
}
