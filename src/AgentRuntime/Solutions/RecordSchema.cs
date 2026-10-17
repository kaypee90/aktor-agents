using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentRuntime.Solutions;

/// <summary>
/// Checks record data against its entity's fields, in plain code: types, required fields, enum
/// values and sizes. The runtime runs this on every write, whoever writes (an agent's tool call,
/// the builder's backend, a seed), so a model can't put a record into a shape the app doesn't
/// expect. <c>end_user</c> fields are the runtime's: whatever a writer sends for them is replaced.
/// </summary>
public static partial class RecordSchema
{
    public const int MaxFields = 60;
    public const int MaxStringLength = 2_000;
    public const int MaxTextLength = 50_000;
    public const int MaxJsonLength = 50_000;
    public const int MaxRecordBytes = 200_000;

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{0,39}$")]
    private static partial Regex EntityNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex FieldNamePattern();

    public static bool IsValidEntityName(string? name) => name is not null && EntityNamePattern().IsMatch(name);

    public static bool IsValidFieldName(string? name) => name is not null && FieldNamePattern().IsMatch(name);

    /// <summary>"WorkOrder" becomes "work_order", for tool names.</summary>
    public static string ToolSlug(string entity)
    {
        var chars = new List<char>();
        for (var i = 0; i < entity.Length; i++)
        {
            var c = entity[i];
            if (char.IsUpper(c) && i > 0) chars.Add('_');
            chars.Add(char.ToLowerInvariant(c));
        }

        return new string([.. chars]);
    }

    /// <summary>The field that says which end user a record belongs to, if the entity has one.</summary>
    public static string? EndUserField(EntityDefinition entity) =>
        entity.Fields.FirstOrDefault(f => f.Value.Type == FieldTypes.EndUser).Key;

    public static string? TitleField(EntityDefinition entity) =>
        entity.TitleField is { } t && entity.Fields.ContainsKey(t) ? t
        : entity.Fields.FirstOrDefault(f => f.Value.Type is FieldTypes.String).Key;

    /// <summary>Problems with an entity's definition (empty when it's usable).</summary>
    public static List<string> ValidateDefinition(string name, EntityDefinition entity, IReadOnlyCollection<string> entityNames)
    {
        var errors = new List<string>();
        if (!IsValidEntityName(name)) errors.Add($"Entity '{name}': names are PascalCase letters and digits (e.g. WorkOrder), 40 at most.");
        if (entity.Fields.Count == 0) errors.Add($"Entity '{name}' has no fields.");
        if (entity.Fields.Count > MaxFields) errors.Add($"Entity '{name}' has more than {MaxFields} fields.");
        if (entity.Fields.Count(f => f.Value.Type == FieldTypes.EndUser) > 1) errors.Add($"Entity '{name}' can have one end_user field at most.");
        if (entity.TitleField is { } title && !entity.Fields.ContainsKey(title)) errors.Add($"Entity '{name}': title_field '{title}' isn't one of its fields.");
        foreach (var (field, def) in entity.Fields)
        {
            var at = $"{name}.{field}";
            if (!IsValidFieldName(field)) errors.Add($"Field '{at}': names are lowercase letters, digits and underscores, starting with a letter.");
            if (field is "id" or "created_at" or "updated_at" or "version") errors.Add($"Field '{at}': '{field}' is kept for the runtime.");
            if (!FieldTypes.All.Contains(def.Type)) errors.Add($"Field '{at}': unknown type '{def.Type}' (use {string.Join(", ", FieldTypes.All)}).");
            if (def.Type == FieldTypes.Enum && (def.Values is not { Count: > 0 } || def.Values.Count > 100)) errors.Add($"Field '{at}': an enum needs 1 to 100 values.");
            if (def.Type == FieldTypes.Ref && (def.Entity is null || !entityNames.Contains(def.Entity))) errors.Add($"Field '{at}': a ref needs 'entity' naming one of the solution's entities.");
            if (def.Type == FieldTypes.EndUser && def.Required) errors.Add($"Field '{at}': an end_user field is set by the runtime and can't be required.");
            if (def.Default is not null && def.Type != FieldTypes.EndUser && Coerce(def, def.Default, out _) is { } defaultError) errors.Add($"Field '{at}': the default isn't valid ({defaultError}).");
        }

        return errors;
    }

    /// <summary>
    /// Validates and normalizes data for a create (<paramref name="existing"/> null) or a partial
    /// update merged over <paramref name="existing"/>. Unknown fields are refused, so a typo is
    /// reported rather than silently stored. Returns the full record data, or the problems.
    /// </summary>
    public static (JsonObject? Data, List<string> Errors) Apply(EntityDefinition entity, JsonObject changes, JsonObject? existing, string? endUserId)
    {
        var errors = new List<string>();
        var data = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        var endUserField = EndUserField(entity);

        foreach (var (field, value) in changes)
        {
            if (!entity.Fields.TryGetValue(field, out var def))
            {
                if (field is "id" or "created_at" or "updated_at" or "version") continue;
                errors.Add($"'{field}' isn't a field (fields: {string.Join(", ", entity.Fields.Keys)}).");
                continue;
            }

            if (def.Type == FieldTypes.EndUser) continue; // the runtime's, below
            if (value is null)
            {
                data.Remove(field);
                continue;
            }

            if (Coerce(def, value, out var normalized) is { } problem)
            {
                errors.Add($"'{field}': {problem}");
                continue;
            }

            data[field] = normalized;
        }

        if (existing is null)
        {
            foreach (var (field, def) in entity.Fields)
            {
                if (!data.ContainsKey(field) && def.Default is not null && def.Type != FieldTypes.EndUser && Coerce(def, def.Default, out var d) is null)
                {
                    data[field] = d;
                }
            }
        }

        // The owner is fixed at creation: by the end user's conversation, or by the backend that
        // created it for someone. Nobody changes it later.
        if (endUserField is not null)
        {
            if (existing is null)
            {
                if (endUserId is not null) data[endUserField] = endUserId;
                else if (changes[endUserField] is JsonValue v && v.TryGetValue<string>(out var owner) && !string.IsNullOrWhiteSpace(owner)) data[endUserField] = owner.Trim();
            }
            else if (existing[endUserField] is { } kept)
            {
                data[endUserField] = kept.DeepClone();
            }
        }

        foreach (var (field, def) in entity.Fields)
        {
            if (def.Required && (data[field] is null || data[field] is JsonValue s && s.TryGetValue<string>(out var str) && string.IsNullOrWhiteSpace(str)))
            {
                errors.Add($"'{field}' is required.");
            }
        }

        if (errors.Count == 0 && data.ToJsonString().Length > MaxRecordBytes) errors.Add($"The record is larger than {MaxRecordBytes / 1000} KB.");
        return errors.Count > 0 ? (null, errors) : (data, errors);
    }

    /// <summary>Converts a value to the field's type (strings like "3" or "true" are accepted
    /// where the meaning is clear, since models often quote numbers). Null when it's valid.</summary>
    public static string? Coerce(FieldDefinition def, JsonNode value, out JsonNode? normalized)
    {
        normalized = null;
        var text = value is JsonValue && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        switch (def.Type)
        {
            case FieldTypes.String or FieldTypes.Text or FieldTypes.Ref or FieldTypes.EndUser:
            {
                var str = text ?? (value is JsonValue ? value.ToJsonString() : null);
                if (str is null) return "expected text";
                var max = def.Type == FieldTypes.Text ? MaxTextLength : MaxStringLength;
                if (str.Length > max) return $"longer than {max} characters";
                normalized = JsonValue.Create(def.Type == FieldTypes.Ref ? str.Trim() : str);
                return null;
            }
            case FieldTypes.Integer:
            {
                // Numbers in any representation (an int, a double, a parsed JSON number), or text.
                var number = value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : text?.Trim();
                if (decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var whole) && whole == decimal.Truncate(whole) &&
                    whole is >= long.MinValue and <= long.MaxValue)
                {
                    normalized = JsonValue.Create((long)whole);
                    return null;
                }

                return "expected a whole number";
            }
            case FieldTypes.Number:
            {
                var number = value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : text?.Trim();
                if (decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                {
                    normalized = JsonValue.Create(m);
                    return null;
                }

                return "expected a number";
            }
            case FieldTypes.Boolean:
                if (value.GetValueKind() is JsonValueKind.True or JsonValueKind.False) { normalized = JsonValue.Create(value.GetValueKind() == JsonValueKind.True); return null; }
                if (text?.Trim().ToLowerInvariant() is "true" or "yes") { normalized = JsonValue.Create(true); return null; }
                if (text?.Trim().ToLowerInvariant() is "false" or "no") { normalized = JsonValue.Create(false); return null; }
                return "expected true or false";
            case FieldTypes.Date:
                if (text is not null && DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    normalized = JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    return null;
                }

                if (text is not null && DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var asDate))
                {
                    normalized = JsonValue.Create(asDate.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    return null;
                }

                return "expected a date (YYYY-MM-DD)";
            case FieldTypes.DateTime:
                if (text is not null && DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                {
                    normalized = JsonValue.Create(dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    return null;
                }

                return "expected a date and time (ISO 8601)";
            case FieldTypes.Enum:
            {
                var choice = text?.Trim();
                var match = def.Values?.FirstOrDefault(v => string.Equals(v, choice, StringComparison.OrdinalIgnoreCase));
                if (match is null) return $"expected one of {string.Join(", ", def.Values ?? [])}";
                normalized = JsonValue.Create(match);
                return null;
            }
            case FieldTypes.Json:
            {
                var json = value.ToJsonString();
                if (json.Length > MaxJsonLength) return $"larger than {MaxJsonLength} characters";
                normalized = value.DeepClone();
                return null;
            }
            default:
                return $"unknown type '{def.Type}'";
        }
    }

    /// <summary>A JSON Schema for the fields a writer may set (for an LLM tool).</summary>
    public static JsonObject FieldsSchema(EntityDefinition entity, bool forUpdate)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (field, def) in entity.Fields)
        {
            if (def.Type == FieldTypes.EndUser) continue;
            var schema = def.Type switch
            {
                FieldTypes.Integer => new JsonObject { ["type"] = "integer" },
                FieldTypes.Number => new JsonObject { ["type"] = "number" },
                FieldTypes.Boolean => new JsonObject { ["type"] = "boolean" },
                FieldTypes.Enum => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. (def.Values ?? []).Select(v => (JsonNode)JsonValue.Create(v)!)]) },
                FieldTypes.Json => new JsonObject(),
                _ => new JsonObject { ["type"] = "string" }
            };
            var description = def.Description ?? string.Empty;
            if (def.Type == FieldTypes.Date) description = (description + " Date as YYYY-MM-DD.").Trim();
            if (def.Type == FieldTypes.DateTime) description = (description + " ISO 8601 date and time.").Trim();
            if (def.Type == FieldTypes.Ref) description = (description + $" The id of a {def.Entity} record.").Trim();
            if (description.Length > 0) schema["description"] = description;
            properties[field] = schema;
            if (def.Required && !forUpdate) required.Add(field);
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    /// <summary>Whether a record's data has each field equal to the value given (compared as text,
    /// case-insensitively), for filters.</summary>
    public static bool Matches(JsonObject data, IReadOnlyDictionary<string, string> where) =>
        where.All(w => data[w.Key] is { } v && string.Equals(AsText(v), w.Value, StringComparison.OrdinalIgnoreCase));

    public static string AsText(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();

    public static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
