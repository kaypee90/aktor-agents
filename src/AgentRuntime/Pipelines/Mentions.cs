using System.Text.RegularExpressions;

namespace AgentRuntime.Pipelines;

/// <summary>A model a person can mention (an organization's model profile, or the server's).</summary>
public sealed record MentionableModel(string Id, string Name, string Provider, string Model);

/// <summary>
/// "@handles" people write in text boxes (docs/workspaces.md#mentions): a pipeline stage by its id
/// (<c>@diagnose</c>), a model by its profile id (<c>@claude-fast</c>, <c>@default-model</c> for
/// the server's), a provider (<c>@anthropic</c>), a skill (<c>@skill:incident-postmortems</c>),
/// knowledge (<c>@knowledge:refund-policy.docx</c>) or a connector (<c>@connector:slack</c>).
/// The dashboard offers them as you type; this turns the ones in a text into a short glossary for
/// the model reading it, so a mention means one thing however capable the model is. An agent whose
/// goal or context names a skill is told to load it first (SkillsSection). Unknown handles are left alone (an email address, say).
/// </summary>
public static partial class Mentions
{
    /// <summary>The handle for the server's own model.</summary>
    public const string DefaultModelHandle = "default-model";

    /// <summary>Skills are mentioned as <c>@skill:name</c>, so they never clash with a stage's id.</summary>
    public const string SkillPrefix = "skill:";

    /// <summary>Knowledge (a file or a fact) is mentioned as <c>@knowledge:handle</c> (<see cref="Memory.KnowledgeFiles.Handle"/>).</summary>
    public const string KnowledgePrefix = "knowledge:";

    /// <summary>A connector (a connection, by name) is mentioned as <c>@connector:name</c>; its tools are <c>name__tool</c>.</summary>
    public const string ConnectorPrefix = "connector:";

    /// <summary>A solution's workspace is mentioned as <c>@workspace:key</c> (in the solution designer's conversation).</summary>
    public const string WorkspacePrefix = "workspace:";

    [GeneratedRegex(@"(?<![\w.@])@([A-Za-z0-9][A-Za-z0-9._:/-]*[A-Za-z0-9]|[A-Za-z0-9])")]
    private static partial Regex Handle();

    /// <summary><paramref name="text"/> with every <c>@oldId</c> mention written as <c>@newId</c>.</summary>
    public static string RenameHandle(string text, string oldId, string newId) =>
        string.IsNullOrEmpty(text) ? text : Handle().Replace(text, m => Same(m.Groups[1].Value, oldId) ? "@" + newId : m.Value);

    /// <summary>A pipeline with a renamed model profile: stages that run on it, and mentions of it in
    /// their instructions. The same pipeline (by reference) when nothing names it.</summary>
    public static PipelineDefinition RenameModel(PipelineDefinition pipeline, string oldId, string newId)
    {
        var changed = false;
        var stages = pipeline.Stages.Select(stage =>
        {
            var instructions = RenameHandle(stage.Instructions, oldId, newId);
            var profile = stage.ModelProfileId == oldId ? newId : stage.ModelProfileId;
            if (instructions == stage.Instructions && profile == stage.ModelProfileId) return stage;
            changed = true;
            return stage with { Instructions = instructions, ModelProfileId = profile };
        }).ToList();
        return changed ? pipeline with { Stages = stages } : pipeline;
    }

    /// <summary>The distinct handles in <paramref name="text"/>, in order, without the @.</summary>
    public static List<string> Find(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : Handle().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The skills mentioned in the texts (<c>@skill:name</c>), by name, lowercase.</summary>
    public static List<string> SkillsIn(IEnumerable<string?> texts) => Prefixed(texts, SkillPrefix);

    /// <summary>The knowledge mentioned in the texts (<c>@knowledge:handle</c>), by handle.</summary>
    public static List<string> KnowledgeIn(IEnumerable<string?> texts) => Prefixed(texts, KnowledgePrefix);

    /// <summary>The connectors mentioned in the texts (<c>@connector:name</c>), by name, lowercase.</summary>
    public static List<string> ConnectorsIn(IEnumerable<string?> texts) => Prefixed(texts, ConnectorPrefix);

    /// <summary>The handles in the texts that start with <paramref name="prefix"/>, without it, lowercase.</summary>
    public static List<string> Prefixed(IEnumerable<string?> texts, string prefix) =>
        texts.SelectMany(Find)
            .Where(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && h.Length > prefix.Length)
            .Select(h => h[prefix.Length..].ToLowerInvariant())
            .Distinct()
            .ToList();

    /// <summary>One line per known handle in the texts, e.g. "@diagnose: the 'Diagnose' stage…".</summary>
    /// <param name="skills">Skills that can be mentioned, as (name, description).</param>
    public static List<string> Describe(IEnumerable<string?> texts, IReadOnlyCollection<PipelineStage> stages,
        IReadOnlyCollection<MentionableModel> models, IReadOnlyCollection<(string Name, string Description)>? skills = null)
    {
        var lines = new List<string>();
        foreach (var handle in texts.SelectMany(Find).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (handle.StartsWith(KnowledgePrefix, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"@{handle}: '{handle[KnowledgePrefix.Length..]}' in the workspace's or organization's knowledge (a document or a fact). " +
                          $"An agent whose instructions mention @{handle} is told to read it with read_knowledge first.");
            }
            else if (handle.StartsWith(ConnectorPrefix, StringComparison.OrdinalIgnoreCase) && handle.Length > ConnectorPrefix.Length)
            {
                var name = handle[ConnectorPrefix.Length..].ToLowerInvariant();
                lines.Add($"@{handle}: the '{name}' connector; its tools are named {name}__<tool>. Use them for the part of the work that mentions it.");
            }
            else if (handle.StartsWith(SkillPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = handle[SkillPrefix.Length..];
                if (skills?.FirstOrDefault(s => Same(s.Name, name)) is { Name: not null } skill)
                {
                    lines.Add($"@{handle}: the '{skill.Name}' skill ({skill.Description}). An agent whose instructions mention @{handle} is told to load it with load_skill and follow it.");
                }
            }
            else if (stages.FirstOrDefault(s => Same(s.StageId, handle)) is { } stage)
            {
                lines.Add($"@{handle}: the '{stage.Name}' stage ({stage.EffectiveRole}), stage id {stage.StageId}.");
            }
            else if (FindModel(handle, models) is { } model)
            {
                lines.Add($"@{handle}: the model '{model.Name}' ({model.Provider} {model.Model}); to run a stage on it, set its model_profile_id to \"{model.Id}\".");
            }
            else if (models.Where(m => Same(m.Provider, handle)).ToList() is { Count: > 0 } byProvider)
            {
                lines.Add($"@{handle}: the {byProvider[0].Provider} provider; its models here: " +
                          string.Join(", ", byProvider.Select(m => $"'{m.Name}' (model_profile_id \"{m.Id}\")")) + ".");
            }
        }

        return lines;
    }

    /// <summary>The model a handle names: a profile id, or the server's model.</summary>
    public static MentionableModel? FindModel(string handle, IReadOnlyCollection<MentionableModel> models) =>
        models.FirstOrDefault(m => Same(m.Id, handle))
        ?? (Same(handle, DefaultModelHandle) ? models.FirstOrDefault(m => Same(m.Id, LLM.ModelProfiles.ServerId)) : null);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
