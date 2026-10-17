using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Solutions;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>
/// Turns an uploaded package (aktor.yaml plus files) into a <see cref="SolutionManifest"/>.
/// The manifest is YAML or JSON in the API's snake_case. A few conveniences keep packages
/// readable: a workspace, eval or the assistant can live in its own file (<c>file: path</c>),
/// long instructions can too (<c>instructions_file: path</c>), and arguments and source
/// arguments may be written as objects instead of JSON strings.
/// </summary>
public static class SolutionManifestReader
{
    public const int MaxFiles = 300;
    public const int MaxBundleChars = 5_000_000;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>JSON written into strings stays readable ("today+60d", not "today\u002B60d").</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static (SolutionManifest? Manifest, List<string> Errors) Read(SolutionBundle bundle)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(bundle.Manifest)) return (null, ["The package has no manifest (aktor.yaml)."]);
        if (bundle.Files.Count > MaxFiles) return (null, [$"A package has at most {MaxFiles} files."]);
        if (bundle.Manifest.Length + bundle.Files.Sum(f => f.Key.Length + (f.Value?.Length ?? 0)) > MaxBundleChars)
        {
            return (null, [$"A package is at most {MaxBundleChars / 1_000_000} MB."]);
        }

        var files = new Dictionary<string, string>();
        foreach (var (path, content) in bundle.Files)
        {
            if (NormalizePath(path) is not { } normal) errors.Add($"'{path}': package paths are relative, without '..'.");
            else files[normal] = content ?? string.Empty;
        }

        if (errors.Count > 0) return (null, errors);

        JsonObject root;
        try
        {
            root = YamlJson.Parse(bundle.Manifest) as JsonObject ?? throw new FormatException("The manifest isn't a mapping of keys to values.");
        }
        catch (FormatException ex)
        {
            return (null, [$"aktor.yaml: {ex.Message}"]);
        }

        Expand(root, files, errors);
        if (errors.Count > 0) return (null, errors);

        try
        {
            var manifest = root.Deserialize<SolutionManifest>(Json);
            return manifest is null ? (null, ["The manifest is empty."]) : (manifest, errors);
        }
        catch (JsonException ex)
        {
            var at = string.IsNullOrEmpty(ex.Path) ? string.Empty : $" at {ex.Path}";
            return (null, [$"aktor.yaml{at}: {ex.Message.Split(". Path:")[0]}"]);
        }
    }

    /// <summary>The package's files as the validator sees them (normalized paths).</summary>
    public static Dictionary<string, string> Files(SolutionBundle bundle) =>
        bundle.Files.Select(f => (Path: NormalizePath(f.Key), f.Value)).Where(f => f.Path is not null).ToDictionary(f => f.Path!, f => f.Value ?? string.Empty);

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        if (p.StartsWith('/') || p.Contains(':') || p.Split('/').Any(s => s is ".." or "")) return null;
        return p.Length <= 300 ? p : null;
    }

    private static void Expand(JsonObject root, IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        JsonObject? Include(JsonNode? item, string what)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var bare)) item = new JsonObject { ["file"] = bare };
            if (item is not JsonObject obj) return item as JsonObject;
            if (obj["file"] is not JsonValue f || !f.TryGetValue<string>(out var path)) return obj;
            if (NormalizePath(path) is not { } normal || !files.TryGetValue(normal, out var text))
            {
                errors.Add($"{what}: '{path}' isn't in the package.");
                return obj;
            }

            JsonObject included;
            try
            {
                included = YamlJson.Parse(text) as JsonObject ?? throw new FormatException("it isn't a mapping of keys to values.");
            }
            catch (FormatException ex)
            {
                errors.Add($"{normal}: {ex.Message}");
                return obj;
            }

            // Keys written next to file: override the file's.
            foreach (var (key, value) in obj)
            {
                if (key != "file") included[key] = value?.DeepClone();
            }

            return included;
        }

        void InstructionsFile(JsonObject? obj, string what)
        {
            if (obj?["instructions_file"] is not JsonValue f || !f.TryGetValue<string>(out var path)) return;
            if (NormalizePath(path) is { } normal && files.TryGetValue(normal, out var text)) obj["instructions"] = text;
            else errors.Add($"{what}: instructions_file '{path}' isn't in the package.");
            obj.Remove("instructions_file");
        }

        if (root["workspaces"] is JsonArray workspaces)
        {
            for (var i = 0; i < workspaces.Count; i++)
            {
                var w = Include(workspaces[i], $"workspaces[{i}]");
                if (!ReferenceEquals(w, workspaces[i])) workspaces[i] = w;
                if (w?["pipeline"]?["stages"] is JsonArray stages)
                {
                    foreach (var stage in stages.OfType<JsonObject>())
                    {
                        InstructionsFile(stage, $"workspaces[{i}] stage '{stage["stage_id"]}'");
                        Stringify(stage, "arguments");
                    }
                }

                if (w?["triggers"] is JsonArray triggers)
                {
                    foreach (var trigger in triggers.OfType<JsonObject>())
                    {
                        if (trigger["source_arguments"] is JsonObject args)
                        {
                            trigger["source_arguments_json"] = args.ToJsonString(Readable);
                            trigger.Remove("source_arguments");
                        }
                    }
                }
            }
        }

        if (root["evals"] is JsonArray evals)
        {
            for (var i = 0; i < evals.Count; i++)
            {
                var e = Include(evals[i], $"evals[{i}]");
                if (!ReferenceEquals(e, evals[i])) evals[i] = e;
            }
        }

        if (root["assistant"] is JsonNode assistant)
        {
            var a = Include(assistant, "assistant");
            InstructionsFile(a, "assistant");
            if (!ReferenceEquals(a, assistant)) root["assistant"] = a;
        }
    }

    private static void Stringify(JsonObject obj, string key)
    {
        if (obj[key] is JsonObject or JsonArray) obj[key] = obj[key]!.ToJsonString(Readable);
    }
}
