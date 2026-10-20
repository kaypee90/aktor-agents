using System.Text.Json.Nodes;

namespace AgentRuntime.Solutions;

/// <summary>
/// Applies <see cref="SolutionEditOp"/>s to a solution draft as the builder holds it: the manifest
/// as snake_case JSON, with knowledge as inline text and skills kept apart (they become SKILL.md
/// files only when the package is written). It only places items; whether the result is a valid
/// solution is the validator's job, run on the result like any package.
/// </summary>
public static class SolutionDraftEditor
{
    public sealed record Result(JsonObject Draft, List<DraftSkill> Skills, List<string> Changes, List<string> Errors);

    private static readonly string[] BasicsFields = ["name", "title", "version", "description", "requires_platform"];

    public static Result Apply(JsonObject draft, IReadOnlyList<DraftSkill> skills, IReadOnlyList<SolutionEditOp> ops)
    {
        var d = (JsonObject)draft.DeepClone();
        var s = skills.ToList();
        var changes = new List<string>();
        var errors = new List<string>();

        foreach (var op in ops)
        {
            var remove = op.Op == "remove";
            if (op.Op is not ("set" or "remove"))
            {
                errors.Add($"Unknown change '{op.Op}' (use set or remove).");
                continue;
            }

            if (!remove && op.Value is null)
            {
                errors.Add($"Setting a {op.Section} needs its value.");
                continue;
            }

            var key = op.Key?.Trim();
            switch (op.Section)
            {
                case "basics":
                    if (remove)
                    {
                        errors.Add("The basics can't be removed.");
                        break;
                    }

                    foreach (var field in BasicsFields.Where(f => op.Value!.ContainsKey(f)))
                    {
                        d[field] = op.Value![field]?.DeepClone();
                        changes.Add($"Set {field.Replace('_', ' ')}");
                    }

                    break;

                case "assistant" or "end_users":
                    if (remove)
                    {
                        if (d.Remove(op.Section)) changes.Add($"Removed the {Label(op.Section)}");
                    }
                    else
                    {
                        var had = d.ContainsKey(op.Section);
                        d[op.Section] = op.Value!.DeepClone();
                        changes.Add($"{(had ? "Updated" : "Added")} the {Label(op.Section)}");
                    }

                    break;

                case "entity" or "seed":
                {
                    if (string.IsNullOrEmpty(key))
                    {
                        errors.Add($"A {op.Section} change needs the entity's name as its key.");
                        break;
                    }

                    var map = Map(d, op.Section == "entity" ? "entities" : "seed");
                    var existing = map.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    if (remove)
                    {
                        if (existing is not null && map.Remove(existing)) changes.Add(op.Section == "entity" ? $"Removed entity {existing}" : $"Removed seed records of {existing}");
                        else errors.Add($"There's no {op.Section} '{key}' to remove.");
                        break;
                    }

                    if (existing is not null && existing != key) map.Remove(existing);
                    // Seed values are { records: [...] } in the edit, a plain list in the manifest.
                    JsonNode value = op.Section == "seed"
                        ? (op.Value!["records"] as JsonArray ?? new JsonArray()).DeepClone()
                        : op.Value!.DeepClone();
                    map[key] = value;
                    changes.Add(op.Section == "entity"
                        ? $"{(existing is null ? "Added" : "Updated")} entity {key}"
                        : $"Set {((JsonArray)value).Count} seed record(s) of {key}");
                    break;
                }

                case "skill":
                {
                    var name = key ?? Text(op.Value, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        errors.Add("A skill change needs the skill's name.");
                        break;
                    }

                    var index = s.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (remove)
                    {
                        if (index < 0) errors.Add($"There's no skill '{name}' to remove.");
                        else
                        {
                            s.RemoveAt(index);
                            changes.Add($"Removed skill {name}");
                        }

                        break;
                    }

                    var skill = new DraftSkill
                    {
                        Name = Text(op.Value, "name") ?? name,
                        Description = Text(op.Value, "description") ?? (index >= 0 ? s[index].Description : string.Empty),
                        Instructions = Text(op.Value, "instructions") ?? (index >= 0 ? s[index].Instructions : string.Empty),
                        Workspace = op.Value!.ContainsKey("workspace") ? Text(op.Value, "workspace") : index >= 0 ? s[index].Workspace : null
                    };
                    if (index >= 0) s[index] = skill;
                    else s.Add(skill);
                    changes.Add($"{(index >= 0 ? "Updated" : "Added")} skill {skill.Name}");
                    break;
                }

                default:
                    if (ListSection(op.Section) is not { } list)
                    {
                        errors.Add($"Unknown section '{op.Section}'. Use one of: {string.Join(", ", SolutionDesignPrompt.Sections)}.");
                        break;
                    }

                    ApplyListItem(d, list.Property, list.IdField, op, key, remove, changes, errors);
                    break;
            }
        }

        return new Result(d, s, changes, errors);
    }

    /// <summary>The list sections: where they live in the manifest and the field that identifies an item.</summary>
    private static (string Property, string IdField)? ListSection(string section) => section switch
    {
        "input" => ("inputs", "key"),
        "connector" => ("connectors", "name"),
        "workspace" => ("workspaces", "key"),
        "knowledge" => ("knowledge", "key"),
        "webhook" => ("webhooks", "url"),
        "eval" => ("evals", "name"),
        _ => null
    };

    private static void ApplyListItem(JsonObject d, string property, string idField, SolutionEditOp op, string? key, bool remove,
        List<string> changes, List<string> errors)
    {
        var id = key ?? Text(op.Value, idField);
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add($"A {op.Section} change needs its {idField.Replace('_', ' ')} as the key.");
            return;
        }

        if (d[property] is not JsonArray list)
        {
            list = [];
            d[property] = list;
        }

        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(Text(list[i] as JsonObject, idField), id, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        var label = $"{op.Section} {id}";
        if (remove)
        {
            if (index < 0) errors.Add($"There's no {label} to remove.");
            else
            {
                list.RemoveAt(index);
                changes.Add($"Removed {label}");
            }

            return;
        }

        var value = (JsonObject)op.Value!.DeepClone();
        // The item names itself: the key wins over a different id inside the value.
        if (!string.Equals(Text(value, idField), id, StringComparison.Ordinal)) value[idField] = id;
        if (index >= 0) list[index] = value;
        else list.Add(value);
        changes.Add($"{(index >= 0 ? "Updated" : "Added")} {label}");
    }

    private static JsonObject Map(JsonObject d, string property)
    {
        if (d[property] is JsonObject map) return map;
        map = [];
        d[property] = map;
        return map;
    }

    private static string? Text(JsonObject? o, string field) =>
        o?[field] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static string Label(string section) => section == "end_users" ? "end-user sign-in" : section;
}
