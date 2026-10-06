using System.Text.RegularExpressions;

namespace AgentRuntime.Pipelines;

/// <summary>A model a person can mention (an organization's model profile, or the server's).</summary>
public sealed record MentionableModel(string Id, string Name, string Provider, string Model);

/// <summary>
/// "@handles" people write in text boxes (docs/workspaces.md#mentions): a pipeline stage by its id
/// (<c>@diagnose</c>), a model by its profile id (<c>@claude-fast</c>, <c>@default-model</c> for
/// the server's), or a provider (<c>@anthropic</c>). The dashboard offers them as you type; this
/// turns the ones in a text into a short glossary for the model reading it, so a mention means one
/// thing however capable the model is. Unknown handles are left alone (an email address, say).
/// </summary>
public static partial class Mentions
{
    /// <summary>The handle for the server's own model.</summary>
    public const string DefaultModelHandle = "default-model";

    [GeneratedRegex(@"(?<![\w.@])@([A-Za-z0-9][A-Za-z0-9._:/-]*[A-Za-z0-9]|[A-Za-z0-9])")]
    private static partial Regex Handle();

    /// <summary>The distinct handles in <paramref name="text"/>, in order, without the @.</summary>
    public static List<string> Find(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : Handle().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>One line per known handle in the texts, e.g. "@diagnose: the 'Diagnose' stage…".</summary>
    public static List<string> Describe(IEnumerable<string?> texts, IReadOnlyCollection<PipelineStage> stages,
        IReadOnlyCollection<MentionableModel> models)
    {
        var lines = new List<string>();
        foreach (var handle in texts.SelectMany(Find).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (stages.FirstOrDefault(s => Same(s.StageId, handle)) is { } stage)
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
