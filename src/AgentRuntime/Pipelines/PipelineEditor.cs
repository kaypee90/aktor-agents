using System.Text.RegularExpressions;
using AgentRuntime.Tools;

namespace AgentRuntime.Pipelines;

/// <summary>
/// Checks a pipeline against the runtime's rules: unique stage ids, inputs that exist, no
/// cycles, known capabilities and limits from <see cref="PipelineOptions"/>. The editor (or the
/// LLM behind it) proposes; this decides.
/// </summary>
public static partial class PipelineValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex StageIdPattern();

    public static bool IsValidStageId(string? id) => id is not null && StageIdPattern().IsMatch(id);

    /// <summary>Every problem with <paramref name="pipeline"/>; empty when it can run.</summary>
    public static List<string> Validate(PipelineDefinition pipeline, PipelineOptions options)
    {
        var errors = new List<string>();
        if (pipeline.Stages.Count == 0) errors.Add("A pipeline needs at least one stage.");
        if (pipeline.Stages.Count > options.MaxStages) errors.Add($"A pipeline can have at most {options.MaxStages} stages.");
        if (pipeline.MaxRunMinutes < 1 || pipeline.MaxRunMinutes > options.MaxRunMinutes)
        {
            errors.Add($"A run's time limit must be between 1 and {options.MaxRunMinutes} minutes.");
        }

        if (pipeline.MaxConcurrentRuns < 1 || pipeline.MaxConcurrentRuns > options.MaxConcurrentRuns)
        {
            errors.Add($"Runs at once must be between 1 and {options.MaxConcurrentRuns}.");
        }

        if (pipeline.ResultUrgency is not ("info" or "warning" or "urgent")) errors.Add("Result urgency must be info, warning or urgent.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in pipeline.Stages)
        {
            var label = string.IsNullOrWhiteSpace(stage.Name) ? stage.StageId : stage.Name;
            if (!IsValidStageId(stage.StageId)) errors.Add($"Stage id '{stage.StageId}' must be lowercase letters, digits and dashes (40 at most).");
            if (!ids.Add(stage.StageId)) errors.Add($"Two stages have the id '{stage.StageId}'.");
            if (string.IsNullOrWhiteSpace(stage.Name)) errors.Add($"Stage '{stage.StageId}' needs a name.");
            if (string.IsNullOrWhiteSpace(stage.Instructions)) errors.Add($"Stage '{label}' needs instructions.");
            if (stage.Instructions.Length > options.MaxInstructionsLength)
            {
                errors.Add($"Stage '{label}' has instructions longer than {options.MaxInstructionsLength} characters.");
            }

            if (stage.MaxHelpers < 0 || stage.MaxHelpers > options.MaxHelpersPerStage)
            {
                errors.Add($"Stage '{label}' may have between 0 and {options.MaxHelpersPerStage} helpers.");
            }

            if (stage.Retries < 0 || stage.Retries > options.MaxRetries) errors.Add($"Stage '{label}' may retry between 0 and {options.MaxRetries} times.");
            if (stage.MaxCostUsd is <= 0) errors.Add($"Stage '{label}' needs a positive cost limit, or none.");
            foreach (var capability in stage.Capabilities.Where(c => !AgentToolCatalog.IsKnownCapability(c)))
            {
                errors.Add($"Stage '{label}' asks for the unknown capability '{capability}'. Known: {string.Join(", ", AgentToolCatalog.KnownCapabilities)}.");
            }

            if (stage.Inputs.Contains(stage.StageId, StringComparer.OrdinalIgnoreCase)) errors.Add($"Stage '{label}' can't take its own result as input.");
        }

        foreach (var stage in pipeline.Stages)
        {
            foreach (var input in stage.Inputs.Where(i => pipeline.Find(i) is null))
            {
                errors.Add($"Stage '{stage.Name}' takes input from '{input}', which isn't a stage.");
            }
        }

        if (errors.Count == 0 && TopologicalOrder(pipeline) is null)
        {
            errors.Add("The stages form a loop: a stage can't (even indirectly) need its own result.");
        }

        return errors;
    }

    /// <summary>The stages in an order where every stage comes after its inputs; null if there's a cycle.</summary>
    public static List<PipelineStage>? TopologicalOrder(PipelineDefinition pipeline)
    {
        var remaining = pipeline.Stages.ToDictionary(s => s.StageId, s => s.Inputs.Count(i => pipeline.Find(i) is not null), StringComparer.OrdinalIgnoreCase);
        var ready = new Queue<PipelineStage>(pipeline.Stages.Where(s => remaining[s.StageId] == 0));
        var order = new List<PipelineStage>();
        while (ready.TryDequeue(out var stage))
        {
            order.Add(stage);
            foreach (var dependent in pipeline.DependentsOf(stage.StageId))
            {
                if (--remaining[dependent.StageId] == 0) ready.Enqueue(dependent);
            }
        }

        return order.Count == pipeline.Stages.Count ? order : null;
    }
}

/// <summary>The outcome of applying edits: the new pipeline, or why it was refused.</summary>
public sealed record PipelineEditResult(PipelineDefinition? Pipeline, List<string> Errors, List<string> Changes)
{
    public bool Success => Errors.Count == 0 && Pipeline is not null;
}

/// <summary>
/// Applies <see cref="PipelineEditOp"/>s to a pipeline, then validates the result. Adding a stage
/// "after X" puts it between X and whatever X fed; "before Y" puts it between Y and Y's inputs;
/// removing a stage joins its inputs to its dependents. That's what lets a person add or remove an
/// agent anywhere in the pipeline without rewiring it by hand.
/// </summary>
public static partial class PipelineEditor
{
    public static PipelineEditResult Apply(PipelineDefinition current, IReadOnlyList<PipelineEditOp> ops, PipelineOptions options)
    {
        var stages = current.Stages.Select(s => s with { Inputs = [.. s.Inputs] }).ToList();
        var layout = new Dictionary<string, StagePosition>(current.Layout, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var changes = new List<string>();
        if (ops.Count == 0) errors.Add("No changes were proposed.");

        PipelineStage? Find(string? id) =>
            id is null ? null : stages.FirstOrDefault(s => string.Equals(s.StageId, id, StringComparison.OrdinalIgnoreCase));
        void Replace(PipelineStage before, PipelineStage after) => stages[stages.IndexOf(before)] = after;
        string NameOf(string id) => Find(id)?.Name ?? id;

        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case PipelineEditOps.AddStage:
                {
                    var patch = op.Stage;
                    if (patch is null || string.IsNullOrWhiteSpace(patch.Name))
                    {
                        errors.Add("add_stage needs a stage with a name.");
                        break;
                    }

                    var id = UniqueId(patch.StageId ?? Slug(patch.Name), stages);
                    var after = Find(op.After);
                    var before = Find(op.Before);
                    if (op.After is not null && after is null) { errors.Add($"There's no stage '{op.After}' to add after."); break; }
                    if (op.Before is not null && before is null) { errors.Add($"There's no stage '{op.Before}' to add before."); break; }

                    var inputs = patch.Inputs?.ToList() ?? [];
                    if (after is not null && !inputs.Contains(after.StageId, StringComparer.OrdinalIgnoreCase)) inputs.Add(after.StageId);
                    if (before is not null && after is null && patch.Inputs is null)
                    {
                        // Before Y alone: take over Y's inputs.
                        inputs.AddRange(before.Inputs);
                    }

                    var stage = ApplyPatch(new PipelineStage { StageId = id, Name = patch.Name.Trim() }, patch with { StageId = null }) with { Inputs = inputs };
                    stages.Add(stage);
                    if (op.Position is { } at) layout[id] = at;

                    if (after is not null && before is not null)
                    {
                        // On the connection after → before: before now takes the new stage instead.
                        var target = Find(before.StageId)!;
                        Replace(target, target with { Inputs = [.. target.Inputs.Where(i => !Same(i, after.StageId)), id] });
                    }
                    else if (after is not null)
                    {
                        // After X: whatever X fed now gets the new stage instead.
                        foreach (var dependent in stages.Where(s => s.StageId != id && s.Inputs.Contains(after.StageId, StringComparer.OrdinalIgnoreCase)).ToList())
                        {
                            Replace(dependent, dependent with { Inputs = [.. dependent.Inputs.Where(i => !Same(i, after.StageId)), id] });
                        }
                    }
                    else if (before is not null)
                    {
                        var target = Find(before.StageId)!;
                        Replace(target, target with { Inputs = [id] });
                    }

                    changes.Add($"Added '{stage.Name}'" + (after is not null ? $" after '{after.Name}'" : "") + (before is not null ? $" before '{before.Name}'" : ""));
                    break;
                }

                case PipelineEditOps.UpdateStage:
                {
                    var stage = Find(op.StageId);
                    if (stage is null || op.Stage is null) { errors.Add($"There's no stage '{op.StageId}' to change."); break; }
                    if (op.Stage.Inputs is { } newInputs && newInputs.Any(i => Find(i) is null))
                    {
                        errors.Add($"'{stage.Name}' can't take input from a stage that doesn't exist.");
                        break;
                    }

                    Replace(stage, ApplyPatch(stage, op.Stage with { StageId = null }));
                    changes.Add($"Changed '{stage.Name}'");
                    break;
                }

                case PipelineEditOps.RemoveStage:
                {
                    var stage = Find(op.StageId);
                    if (stage is null) { errors.Add($"There's no stage '{op.StageId}' to remove."); break; }
                    stages.Remove(stage);
                    foreach (var dependent in stages.Where(s => s.Inputs.Contains(stage.StageId, StringComparer.OrdinalIgnoreCase)).ToList())
                    {
                        var bridged = dependent.Inputs.Where(i => !Same(i, stage.StageId))
                            .Concat(stage.Inputs.Where(i => !dependent.Inputs.Contains(i, StringComparer.OrdinalIgnoreCase)))
                            .ToList();
                        Replace(dependent, dependent with { Inputs = bridged });
                    }

                    changes.Add($"Removed '{stage.Name}'");
                    break;
                }

                case PipelineEditOps.Connect:
                {
                    var from = Find(op.From);
                    var to = Find(op.To);
                    if (from is null || to is null) { errors.Add($"Can't connect '{op.From}' to '{op.To}': both must be stages."); break; }
                    if (!to.Inputs.Contains(from.StageId, StringComparer.OrdinalIgnoreCase))
                    {
                        Replace(to, to with { Inputs = [.. to.Inputs, from.StageId] });
                        changes.Add($"Connected '{from.Name}' to '{to.Name}'");
                    }

                    break;
                }

                case PipelineEditOps.Disconnect:
                {
                    var to = Find(op.To);
                    if (to is null || op.From is null || !to.Inputs.Contains(op.From, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add($"There's no connection from '{op.From}' to '{op.To}'.");
                        break;
                    }

                    Replace(to, to with { Inputs = [.. to.Inputs.Where(i => !Same(i, op.From))] });
                    changes.Add($"Disconnected '{NameOf(op.From)}' from '{to.Name}'");
                    break;
                }

                default:
                    errors.Add($"Unknown change '{op.Op}'. Use one of: {string.Join(", ", PipelineEditOps.All)}.");
                    break;
            }
        }

        var next = current with { Stages = stages, Layout = PipelineLayout.Keep(layout, stages) };
        if (errors.Count == 0) errors.AddRange(PipelineValidator.Validate(next, options));
        return new PipelineEditResult(errors.Count == 0 ? next : null, errors, changes);
    }

    private static PipelineStage ApplyPatch(PipelineStage stage, PipelineStagePatch patch) => stage with
    {
        Name = patch.Name?.Trim() ?? stage.Name,
        Role = patch.Role?.Trim() ?? stage.Role,
        Instructions = patch.Instructions?.Trim() ?? stage.Instructions,
        Inputs = patch.Inputs?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? stage.Inputs,
        Capabilities = patch.Capabilities?.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList() ?? stage.Capabilities,
        ModelProfileId = patch.ModelProfileId is null ? stage.ModelProfileId : patch.ModelProfileId.Length == 0 ? null : patch.ModelProfileId,
        MaxHelpers = patch.MaxHelpers ?? stage.MaxHelpers,
        MayMessageStages = patch.MayMessageStages ?? stage.MayMessageStages,
        Retries = patch.Retries ?? stage.Retries,
        OnFailure = patch.OnFailure ?? stage.OnFailure,
        MaxCostUsd = patch.MaxCostUsd is null ? stage.MaxCostUsd : patch.MaxCostUsd <= 0 ? null : patch.MaxCostUsd
    };

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    /// <summary>"Security Reviewer" → "security-reviewer".</summary>
    public static string Slug(string name)
    {
        var slug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        return slug.Length == 0 ? "stage" : slug;
    }

    private static string UniqueId(string wanted, List<PipelineStage> stages)
    {
        var id = PipelineValidator.IsValidStageId(wanted) ? wanted : Slug(wanted);
        var candidate = id;
        for (var n = 2; stages.Any(s => Same(s.StageId, candidate)); n++) candidate = $"{id}-{n}";
        return candidate;
    }
}

/// <summary>Canvas positions of a pipeline's stages (<see cref="PipelineDefinition.Layout"/>).</summary>
public static class PipelineLayout
{
    /// <summary>Positions on the canvas are kept within this distance of the origin.</summary>
    public const double MaxCoordinate = 100_000;

    /// <summary>The positions of stages that exist, with sane coordinates.</summary>
    public static Dictionary<string, StagePosition> Keep(IReadOnlyDictionary<string, StagePosition> layout, IEnumerable<PipelineStage> stages)
    {
        var ids = stages.Select(s => s.StageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return layout
            .Where(p => ids.Contains(p.Key) && double.IsFinite(p.Value.X) && double.IsFinite(p.Value.Y))
            .ToDictionary(p => p.Key, p => new StagePosition(
                Math.Round(Math.Clamp(p.Value.X, -MaxCoordinate, MaxCoordinate)),
                Math.Round(Math.Clamp(p.Value.Y, -MaxCoordinate, MaxCoordinate))));
    }
}
