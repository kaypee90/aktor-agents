using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.Integrations;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Solutions;

/// <summary>What validating a manifest found: errors stop an install, warnings don't.</summary>
public sealed record SolutionValidation
{
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Checks a whole manifest before anything is installed, so a bad package is refused with every
/// problem listed rather than half-installed. Pipelines, forms, watches and schedules are checked
/// by the same rules the workspace applies when they're added by hand.
/// </summary>
public static partial class SolutionValidator
{
    /// <summary>The connection every workspace of a solution gets for its records.</summary>
    public const string DataConnectionName = "data";

    public const int MaxWorkspaces = 20;
    public const int MaxEntities = 40;
    public const int MaxSeedRecords = 1_000;
    public const int MaxIndexedFields = 20;

    /// <summary>The solution platform's version: what a manifest's requires_platform is checked
    /// against. It follows the /api/v1 contract: minor versions only add.</summary>
    public const string PlatformVersion = "1.3.0";

    [GeneratedRegex("^[a-z][a-z0-9-]{1,49}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemverPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex KeyPattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static bool IsValidVersion(string? version) => version is not null && SemverPattern().IsMatch(version);

    /// <summary>Compares semantic versions by their numbers (a pre-release sorts before its release).</summary>
    public static int CompareVersions(string a, string b)
    {
        static (int[] Numbers, string Pre) Parse(string v)
        {
            var dash = v.IndexOf('-');
            var core = dash < 0 ? v : v[..dash];
            var numbers = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            return (numbers, dash < 0 ? string.Empty : v[(dash + 1)..]);
        }

        var (na, pa) = Parse(a);
        var (nb, pb) = Parse(b);
        for (var i = 0; i < 3; i++)
        {
            var c = na.ElementAtOrDefault(i).CompareTo(nb.ElementAtOrDefault(i));
            if (c != 0) return c;
        }

        if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length);
        return string.CompareOrdinal(pa, pb);
    }

    public static SolutionValidation Validate(SolutionManifest m, Func<string, bool> pluginExists, Pipelines.PipelineOptions pipelineOptions,
        IReadOnlyDictionary<string, string>? files = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        files ??= new Dictionary<string, string>();

        if (m.Format != 1) errors.Add($"Unknown manifest format {m.Format} (this server reads format 1).");
        if (!IsValidName(m.Name)) errors.Add("'name' is required: lowercase letters, digits and dashes, starting with a letter (2 to 50).");
        if (!IsValidVersion(m.Version)) errors.Add("'version' is required, as a semantic version like 1.0.0.");
        if (m.Title?.Length > 100) errors.Add("'title' is at most 100 characters.");
        if (m.Description?.Length > 2000) errors.Add("'description' is at most 2,000 characters.");
        if (m.RequiresPlatform is { } requires && PlatformMismatch(requires) is { } mismatch) errors.Add(mismatch);

        // Inputs become workspace variables.
        var inputKeys = new HashSet<string>();
        foreach (var input in m.Inputs)
        {
            if (!WorkspaceVariables.IsValidName(input.Key)) errors.Add($"Input '{input.Key}': keys are lowercase letters, digits and underscores, starting with a letter.");
            else if (!inputKeys.Add(input.Key)) errors.Add($"Input '{input.Key}' is declared twice.");
            if (input.Type is not ("string" or "text" or "number" or "integer" or "boolean")) errors.Add($"Input '{input.Key}': type is string, text, number, integer or boolean.");
            if (input.Options is { Count: > 0 } options && input.Default is { } d && !options.Contains(d)) errors.Add($"Input '{input.Key}': the default isn't one of its options.");
        }

        if (m.Inputs.Count > WorkspaceVariables.MaxCount) errors.Add($"A solution can have at most {WorkspaceVariables.MaxCount} inputs.");

        var connectorNames = new HashSet<string>();
        foreach (var c in m.Connectors)
        {
            if (c.Name != ConnectionNames.Slug(c.Name)) errors.Add($"Connector '{c.Name}': names are lowercase letters, digits and underscores (24 at most).");
            else if (c.Name == DataConnectionName) errors.Add($"Connector '{c.Name}': '{DataConnectionName}' is the solution's own records connection.");
            else if (!connectorNames.Add(c.Name)) errors.Add($"Connector '{c.Name}' is declared twice.");
            if (string.IsNullOrWhiteSpace(c.Plugin)) errors.Add($"Connector '{c.Name}' needs a plugin.");
            else if (!pluginExists(c.Plugin)) (c.Required ? errors : warnings).Add($"Connector '{c.Name}': no plugin '{c.Plugin}' is installed on this server.");
            foreach (var key in Placeholders(c.Settings.Values).Where(k => !inputKeys.Contains(k)))
            {
                errors.Add($"Connector '{c.Name}' uses {{{{ inputs.{key} }}}}, which isn't an input.");
            }
        }

        // Entities.
        if (m.Entities.Count > MaxEntities) errors.Add($"A solution can have at most {MaxEntities} entities.");
        if (m.Entities.Sum(e => e.Value.Fields.Count(f => f.Value.Indexed)) > MaxIndexedFields) errors.Add($"A solution can index at most {MaxIndexedFields} fields.");
        foreach (var (entity, field) in m.Entities.SelectMany(e => e.Value.Fields.Where(f => f.Value.Indexed && f.Value.Type is FieldTypes.Json or FieldTypes.Text).Select(f => (e.Key, f.Key))))
        {
            errors.Add($"Field '{entity}.{field}': json and text fields can't be indexed.");
        }

        var entityNames = m.Entities.Keys.ToHashSet();
        foreach (var (name, entity) in m.Entities) errors.AddRange(RecordSchema.ValidateDefinition(name, entity, entityNames));

        void CheckData(string where, Dictionary<string, string> data)
        {
            foreach (var (entity, access) in data)
            {
                if (!entityNames.Contains(entity)) errors.Add($"{where}: data access to '{entity}', which isn't an entity.");
                if (!EntityAccessNames.IsValid(access)) errors.Add($"{where}: access to '{entity}' is 'read' or 'write'.");
            }
        }

        void CheckConnectors(string where, IEnumerable<string> names)
        {
            foreach (var name in names.Where(n => !connectorNames.Contains(n))) errors.Add($"{where} uses connector '{name}', which isn't declared under connectors.");
        }

        // Workspaces.
        if (m.Workspaces.Count == 0 && m.Assistant is null) errors.Add("A solution needs at least one workspace or an assistant.");
        if (m.Workspaces.Count > MaxWorkspaces) errors.Add($"A solution can have at most {MaxWorkspaces} workspaces.");
        var workspaceKeys = new HashSet<string>();
        foreach (var w in m.Workspaces)
        {
            var at = $"Workspace '{(string.IsNullOrWhiteSpace(w.Key) ? w.Name : w.Key)}'";
            if (!KeyPattern().IsMatch(w.Key ?? string.Empty)) errors.Add($"{at}: 'key' is required (lowercase letters, digits and dashes); upgrades match workspaces by it.");
            else if (!workspaceKeys.Add(w.Key)) errors.Add($"{at}: the key is used twice.");
            if (string.IsNullOrWhiteSpace(w.Name) || w.Name.Length > 100) errors.Add($"{at}: 'name' is required (100 characters at most).");
            if (string.IsNullOrWhiteSpace(w.Goal)) errors.Add($"{at}: 'goal' is required.");
            if (w.Pipeline is { } pipeline)
            {
                foreach (var problem in Pipelines.PipelineValidator.Validate(pipeline, pipelineOptions)) errors.Add($"{at}: pipeline: {problem}");
            }

            var triggerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in w.Triggers)
            {
                var tat = $"{at}, trigger '{t.Name}'";
                if (string.IsNullOrWhiteSpace(t.Name)) errors.Add($"{at}: every trigger needs a name.");
                else if (!triggerNames.Add(t.Name.Trim())) errors.Add($"{tat}: trigger names must be unique in a workspace (upgrades match triggers by name).");
                switch (t.Kind)
                {
                    case TriggerKind.Record:
                        if (RecordTriggers.Validate(t) is { } recordError) errors.Add($"{tat}: {recordError}");
                        else if (!entityNames.Contains(t.Entity!.Trim())) errors.Add($"{tat}: '{t.Entity}' isn't an entity.");
                        break;
                    case TriggerKind.Form:
                        if (FormValidator.Validate(t.Form) is { } formError) errors.Add($"{tat}: {formError}");
                        break;
                    case TriggerKind.Schedule:
                        if (!string.IsNullOrWhiteSpace(t.Cron) ? !CronSchedule.TryParse(t.Cron, out _, out _) : t.EveryMinutes is not > 0)
                        {
                            errors.Add($"{tat}: a schedule needs a valid cron expression or every_minutes.");
                        }

                        break;
                    case TriggerKind.Watch:
                        if (string.IsNullOrWhiteSpace(t.SourceTool) || t.Rule is null) errors.Add($"{tat}: a watch needs source_tool and a rule.");
                        else if (WatchEvaluator.Validate(t.Rule) is { } watchError) errors.Add($"{tat}: {watchError}");
                        else if (!string.IsNullOrWhiteSpace(t.Cron) ? !CronSchedule.TryParse(t.Cron, out _, out _) : t.EveryMinutes is not > 0)
                        {
                            errors.Add($"{tat}: a watch needs a valid cron expression or every_minutes.");
                        }

                        break;
                }
            }

            CheckConnectors(at, w.Connectors);
            CheckData(at, w.Data);
            foreach (var problem in WorkspaceVariables.Validate(w.Variables)) errors.Add($"{at}: {problem}");
            foreach (var key in w.Variables.Keys.Where(inputKeys.Contains)) warnings.Add($"{at}: variable '{key}' has the same name as an input; the input's value wins.");
            if (w.Pipeline is null) warnings.Add($"{at}: no pipeline, so it gets one stage that does its goal.");
        }

        // Skills and knowledge come from the package's files.
        foreach (var skill in m.Skills)
        {
            if (!files.ContainsKey(skill.Path)) errors.Add($"Skill '{skill.Path}': the file isn't in the package.");
            if (skill.Workspace is { } ws && !workspaceKeys.Contains(ws)) errors.Add($"Skill '{skill.Path}': workspace '{ws}' isn't one of the solution's.");
        }

        var knowledgeKeys = new HashSet<string>();
        foreach (var k in m.Knowledge)
        {
            if (string.IsNullOrWhiteSpace(k.Key) || k.Key.Length > 200) errors.Add("Every knowledge entry needs a key (200 characters at most).");
            else if (!knowledgeKeys.Add(k.Key)) errors.Add($"Knowledge '{k.Key}' is declared twice.");
            if (k.Path is null && string.IsNullOrWhiteSpace(k.Text)) errors.Add($"Knowledge '{k.Key}' needs a path or text.");
            if (k.Path is { } path && !files.ContainsKey(path)) errors.Add($"Knowledge '{k.Key}': '{path}' isn't in the package.");
            if (k.Workspace is { } ws && !workspaceKeys.Contains(ws)) errors.Add($"Knowledge '{k.Key}': workspace '{ws}' isn't one of the solution's.");
        }

        if (m.Assistant is { } a)
        {
            if (string.IsNullOrWhiteSpace(a.Instructions)) errors.Add("assistant: 'instructions' are required.");
            if (a.Instructions.Length > 20_000) errors.Add("assistant: 'instructions' are at most 20,000 characters.");
            if (a.MaxTokens is < 1_000 or > 2_000_000) errors.Add("assistant: max_tokens is between 1,000 and 2,000,000.");
            if (a.MaxCostUsd is <= 0 or > 100) errors.Add("assistant: max_cost_usd is between 0 and 100.");
            if (a.MaxConversationsPerDay is < 1 or > 10_000) errors.Add("assistant: max_conversations_per_day is between 1 and 10,000.");
            if (a.MaxMessagesPerMinute is < 1 or > 600) errors.Add("assistant: max_messages_per_minute is between 1 and 600.");
            if (a.MonthlyCostLimitUsdPerUser is <= 0) errors.Add("assistant: monthly_cost_limit_usd_per_user is more than 0 (leave it out for no cap).");
            CheckConnectors("assistant", a.Connectors);
            foreach (var origin in a.AllowedOrigins)
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || o.Scheme is not ("https" or "http") || o.AbsolutePath != "/" || origin.TrimEnd('/') != o.GetLeftPart(UriPartial.Authority))
                {
                    errors.Add($"assistant: allowed origin '{origin}' should be just a scheme and host, like https://app.example.com.");
                }
            }
            foreach (var key in Placeholders([a.Instructions, a.Greeting ?? string.Empty]).Where(k => !inputKeys.Contains(k)))
            {
                errors.Add($"assistant uses {{{{ inputs.{key} }}}}, which isn't an input.");
            }

            CheckData("assistant", a.Data);
            if (a.Data.Count == 0 && a.Connectors.Count == 0) warnings.Add("assistant: it has no data access and no connectors, so it can only talk.");
        }

        if (m.EndUsers?.Jwt is { } jwt)
        {
            foreach (var (label, value) in new[] { ("issuer", jwt.Issuer), ("audience", jwt.Audience), ("jwks_url", jwt.JwksUrl), ("user_claim", jwt.UserClaim) })
            {
                if (string.IsNullOrWhiteSpace(value)) errors.Add($"end_users.jwt: '{label}' is required.");
            }

            var jwks = ReplaceInputs(jwt.JwksUrl, m.Inputs.ToDictionary(i => i.Key, i => i.Default ?? "https://example.com/jwks"));
            if (!string.IsNullOrWhiteSpace(jwt.JwksUrl) && (!Uri.TryCreate(jwks, UriKind.Absolute, out var u) || !(u.Scheme == "https" || u.Scheme == "http" && u.IsLoopback)))
            {
                errors.Add("end_users.jwt: 'jwks_url' must be an https URL (http only on this machine, for development).");
            }

            foreach (var key in Placeholders([jwt.Issuer, jwt.Audience, jwt.JwksUrl]).Where(k => !inputKeys.Contains(k))) errors.Add($"end_users.jwt uses {{{{ inputs.{key} }}}}, which isn't an input.");
            if (m.Assistant is null) warnings.Add("end_users.jwt is set, but there's no assistant for end users to talk to.");
        }

        var seedCount = 0;
        foreach (var (entity, records) in m.Seed)
        {
            if (!m.Entities.TryGetValue(entity, out var def))
            {
                errors.Add($"seed: '{entity}' isn't an entity.");
                continue;
            }

            seedCount += records.Count;
            for (var i = 0; i < records.Count; i++)
            {
                var (_, problems) = RecordSchema.Apply(def, records[i], null, null);
                foreach (var problem in problems.Take(3)) errors.Add($"seed: {entity}[{i}]: {problem}");
            }
        }

        if (seedCount > MaxSeedRecords) errors.Add($"seed: at most {MaxSeedRecords} records.");

        foreach (var hook in m.Webhooks)
        {
            var url = ReplaceInputs(hook.Url, m.Inputs.ToDictionary(i => i.Key, i => i.Default ?? "https://example.com"));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) errors.Add($"webhook '{hook.Url}': an http(s) URL is required.");
            foreach (var e in hook.Events.Where(e => e is not ("record.*" or "record.created" or "record.updated" or "record.deleted")))
            {
                errors.Add($"webhook '{hook.Url}': unknown event '{e}' (record.created, record.updated, record.deleted or record.*).");
            }

            foreach (var e in hook.Entities.Where(e => !entityNames.Contains(e))) errors.Add($"webhook '{hook.Url}': '{e}' isn't an entity.");
            foreach (var key in Placeholders([hook.Url]).Where(k => !inputKeys.Contains(k))) errors.Add($"webhook '{hook.Url}' uses {{{{ inputs.{key} }}}}, which isn't an input.");
        }

        var evalNames = new HashSet<string>();
        foreach (var e in m.Evals)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) errors.Add("Every eval needs a name.");
            else if (!evalNames.Add(e.Name)) errors.Add($"Eval '{e.Name}' is declared twice.");
            if (!workspaceKeys.Contains(e.Workspace)) errors.Add($"Eval '{e.Name}': workspace '{e.Workspace}' isn't one of the solution's.");
            if (e.TimeoutSeconds is < 10 or > 3600) errors.Add($"Eval '{e.Name}': timeout_seconds is between 10 and 3,600.");
            foreach (var entity in e.Expect.Records.Keys.Where(k => !entityNames.Contains(k))) errors.Add($"Eval '{e.Name}': '{entity}' isn't an entity.");
        }

        CheckMentions(m, files, connectorNames, warnings);
        return new SolutionValidation { Errors = errors, Warnings = warnings };
    }

    /// <summary>
    /// @mentions in what agents read (docs/solutions.md#mentions): a skill, knowledge or connector
    /// mentioned but not in the package is a typo, or something the organization must already have,
    /// and a connector mentioned in a workspace that doesn't use it gives its agents no tools. Warnings
    /// only: the organization's own skills and knowledge are fine to mention.
    /// </summary>
    private static void CheckMentions(SolutionManifest m, IReadOnlyDictionary<string, string> files, HashSet<string> connectorNames, List<string> warnings)
    {
        var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in m.Skills.Where(s => files.ContainsKey(s.Path)))
        {
            try
            {
                skillNames.Add(Skills.SkillPackage.FromMarkdown(files[skill.Path]).Name);
            }
            catch (Skills.SkillPackageException)
            {
                // Reported where skills are checked.
            }
        }

        var knowledge = m.Knowledge.Select(k => Memory.KnowledgeFiles.Handle(k.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Check(string where, IReadOnlyList<string?> texts, ICollection<string>? used, bool getsPackageKnowhow)
        {
            foreach (var name in Pipelines.Mentions.SkillsIn(texts).Where(n => !getsPackageKnowhow || !skillNames.Contains(n)))
            {
                warnings.Add(getsPackageKnowhow
                    ? $"{where} mentions @skill:{name}, which isn't one of the solution's skills: it works only if the organization has it."
                    : $"{where} mentions @skill:{name}: the solution's skills go to its workspaces, so it works only if the organization has it.");
            }

            foreach (var handle in Pipelines.Mentions.KnowledgeIn(texts).Where(h => !getsPackageKnowhow || !knowledge.Contains(h)))
            {
                warnings.Add(getsPackageKnowhow
                    ? $"{where} mentions @knowledge:{handle}, which isn't the solution's knowledge: it works only if the organization has it."
                    : $"{where} mentions @knowledge:{handle}: the solution's knowledge goes to its workspaces, so it works only if the organization has it.");
            }

            foreach (var name in Pipelines.Mentions.ConnectorsIn(texts))
            {
                if (!connectorNames.Contains(name)) warnings.Add($"{where} mentions @connector:{name}, which isn't declared under connectors.");
                else if (used is not null && !used.Contains(name)) warnings.Add($"{where} mentions @connector:{name} but doesn't use that connector, so its agents won't have its tools.");
            }
        }

        foreach (var w in m.Workspaces)
        {
            var texts = new List<string?> { w.Goal };
            texts.AddRange(w.Pipeline?.Stages.Select(s => s.Instructions) ?? []);
            texts.AddRange(w.Triggers.Select(t => t.Instruction));
            texts.AddRange(m.Evals.Where(e => e.Workspace == w.Key).Select(e => e.Input));
            Check($"Workspace '{w.Key}'", texts, w.Connectors, true);
        }

        if (m.Assistant is { } a) Check("assistant", [a.Instructions], a.Connectors, false);
    }

    /// <summary>Why this server can't run a solution that requires <paramref name="requirement"/>
    /// (">=1.0.0", ">1.0.0", "=1.1.0", "^1.0.0" or "1.0.0"); null when it can.</summary>
    public static string? PlatformMismatch(string requirement)
    {
        var r = requirement.Trim();
        var (op, version) = r.StartsWith(">=") ? (">=", r[2..]) : r.StartsWith('>') ? (">", r[1..]) : r.StartsWith('=') ? ("=", r[1..])
            : r.StartsWith('^') ? ("^", r[1..]) : ("^", r);
        version = version.Trim();
        if (!IsValidVersion(version)) return $"'requires_platform' is like \">=1.0.0\" (got '{requirement}').";
        var c = CompareVersions(PlatformVersion, version);
        var ok = op switch
        {
            ">=" => c >= 0,
            ">" => c > 0,
            "=" => c == 0,
            _ => c >= 0 && version.Split('.')[0] == PlatformVersion.Split('.')[0]
        };
        return ok ? null : $"This solution requires platform {requirement}; this server is {PlatformVersion}.";
    }

    /// <summary>The input keys named by <c>{{ inputs.key }}</c> placeholders.</summary>
    public static IEnumerable<string> Placeholders(IEnumerable<string> values) =>
        values.SelectMany(v => InputPlaceholder().Matches(v ?? string.Empty).Select(m => m.Groups[1].Value)).Distinct();

    public static string ReplaceInputs(string value, IReadOnlyDictionary<string, string> inputs) =>
        InputPlaceholder().Replace(value, m => inputs.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    [GeneratedRegex(@"\{\{\s*inputs\.([a-z][a-z0-9_]*)\s*\}\}")]
    private static partial Regex InputPlaceholder();

    /// <summary>The values to install with: what the installer gave, else each input's default.
    /// Errors for required inputs without a value and values of the wrong type.</summary>
    public static (Dictionary<string, string> Values, List<string> Errors) ResolveInputs(SolutionManifest m, IReadOnlyDictionary<string, string>? given,
        IReadOnlyDictionary<string, string>? previous = null)
    {
        var values = new Dictionary<string, string>();
        var errors = new List<string>();
        foreach (var input in m.Inputs)
        {
            var value = given?.GetValueOrDefault(input.Key) ?? previous?.GetValueOrDefault(input.Key) ?? input.Default;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (input.Required) errors.Add($"Input '{input.Key}' is required.");
                continue;
            }

            value = value.Trim();
            var ok = input.Type switch
            {
                "number" => decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _),
                "integer" => long.TryParse(value, out _),
                "boolean" => value is "true" or "false",
                "string" => value.Length <= 500,
                _ => value.Length <= WorkspaceVariables.MaxValueLength
            };
            if (!ok) errors.Add($"Input '{input.Key}' should be {(input.Type is "string" ? "at most 500 characters" : input.Type == "boolean" ? "true or false" : $"a{(input.Type == "integer" ? "n" : "")} {input.Type}")}.");
            else if (input.Options is { Count: > 0 } options && !options.Contains(value)) errors.Add($"Input '{input.Key}' should be one of {string.Join(", ", options)}.");
            else values[input.Key] = value;
        }

        foreach (var key in given?.Keys.Where(k => m.Inputs.All(i => i.Key != k)) ?? []) errors.Add($"'{key}' isn't an input of this solution.");
        return (values, errors);
    }

    /// <summary>A short description of what the manifest installs.</summary>
    public static JsonObject Summarize(SolutionManifest m) => new()
    {
        ["name"] = m.Name,
        ["title"] = m.DisplayTitle,
        ["version"] = m.Version,
        ["workspaces"] = new JsonArray([.. m.Workspaces.Select(w => (JsonNode)new JsonObject
        {
            ["key"] = w.Key,
            ["name"] = w.Name,
            ["stages"] = w.Pipeline?.Stages.Count ?? 1,
            ["triggers"] = new JsonArray([.. w.Triggers.Select(t => (JsonNode)JsonValue.Create($"{t.Kind}: {t.Name}")!)])
        })]),
        ["entities"] = new JsonArray([.. m.Entities.Select(e => (JsonNode)new JsonObject { ["name"] = e.Key, ["fields"] = e.Value.Fields.Count })]),
        ["connectors"] = new JsonArray([.. m.Connectors.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["plugin"] = c.Plugin, ["required"] = c.Required })]),
        ["inputs"] = new JsonArray([.. m.Inputs.Select(i => (JsonNode)new JsonObject
        {
            ["key"] = i.Key, ["label"] = i.Label ?? i.Key, ["type"] = i.Type, ["required"] = i.Required, ["default"] = i.Default, ["description"] = i.Description
        })]),
        ["skills"] = m.Skills.Count,
        ["knowledge"] = m.Knowledge.Count,
        ["assistant"] = m.Assistant?.Name,
        ["evals"] = m.Evals.Count
    };
}
