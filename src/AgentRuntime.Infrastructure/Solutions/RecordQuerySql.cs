using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentRuntime.Solutions;
using Npgsql;
using NpgsqlTypes;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>
/// A records query as SQL, so every filter and sort runs in Postgres over the whole table, not
/// over a sample. Values are always parameters. Field names are the entity's own, checked against
/// its definition and the field-name pattern, and written as SQL literals so the expressions match
/// the per-field indexes (<see cref="IndexDdl"/>) exactly. Numbers compare as numbers, everything
/// else as text in byte order (ISO dates sort correctly); equality and "contains" ignore case.
/// </summary>
public static class RecordQuerySql
{
    public const int MaxLimit = 200;

    public sealed record Command(string Sql, object[] Parameters);

    public static Command Build(RecordScope scope, string entity, EntityDefinition definition, RecordQuery query, DateTimeOffset now, bool page)
    {
        var parameters = new List<NpgsqlParameter>();
        string P(object value, NpgsqlDbType? type = null)
        {
            var name = "q" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            var p = new NpgsqlParameter(name, value);
            if (type is { } t) p.NpgsqlDbType = t;
            parameters.Add(p);
            return "@" + name;
        }

        var sql = new StringBuilder("SELECT * FROM \"SolutionRecords\" WHERE \"TenantId\" = ")
            .Append(P(scope.TenantId)).Append(" AND \"InstallId\" = ").Append(P(scope.InstallId)).Append(" AND \"Entity\" = ").Append(P(entity));
        if (scope.EndUserId is { } user && RecordSchema.EndUserField(definition) is not null) sql.Append(" AND \"EndUserId\" = ").Append(P(user));

        FieldDefinition Field(string name) =>
            definition.Fields.TryGetValue(name, out var def) ? def : throw new ArgumentException($"'{name}' isn't a field of {entity} (fields: {string.Join(", ", definition.Fields.Keys)}).");

        // Exact matches: a JSONB containment test with the value in the field's own type, which
        // the GIN index on the data serves.
        foreach (var (field, value) in query.Where)
        {
            if (field == "id")
            {
                sql.Append(" AND \"RecordId\" = ").Append(P(value));
                continue;
            }

            var def = Field(field);
            if (RecordSchema.Coerce(def, JsonValue.Create(value)!, out var typed) is { } problem) throw new ArgumentException($"'{field}': {problem}");
            sql.Append(" AND \"DataJson\" @> ").Append(P(new JsonObject { [field] = typed }.ToJsonString(), NpgsqlDbType.Jsonb));
        }

        foreach (var c in query.Filters)
        {
            var def = Field(c.Field);
            var f = Literal(c.Field);
            var value = c.Value is null ? null : RelativeDates.Resolve(c.Value, now);
            var numeric = def.Type is FieldTypes.Integer or FieldTypes.Number;
            var text = TextOf(f);
            switch (c.Op)
            {
                case "exists":
                    sql.Append($" AND coalesce(jsonb_typeof(\"DataJson\" -> {f}), 'null') <> 'null'");
                    break;
                case "not_exists":
                    sql.Append($" AND coalesce(jsonb_typeof(\"DataJson\" -> {f}), 'null') = 'null'");
                    break;
                case "contains" or "not_contains":
                {
                    var pattern = "%" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                    var like = $"(\"DataJson\" ->> {f}) ILIKE {P(pattern)} ESCAPE '\\'";
                    sql.Append(c.Op == "contains" ? $" AND {like}" : $" AND NOT coalesce({like}, false)");
                    break;
                }
                case "<" or "<=" or ">" or ">=" or "==" or "!=":
                {
                    if (value is null) throw new ArgumentException($"The filter on '{c.Field}' needs a value.");
                    var op = c.Op == "==" ? "=" : c.Op;
                    if (numeric)
                    {
                        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        {
                            throw new ArgumentException($"'{c.Field}' is a number; '{value}' isn't.");
                        }

                        var expr = NumberOf(f);
                        sql.Append(op == "!=" ? $" AND {expr} IS DISTINCT FROM {P(number)}" : $" AND {expr} {op} {P(number)}");
                    }
                    else
                    {
                        var expr = text;
                        var v = $"{P(value.ToLowerInvariant())} COLLATE \"C\"";
                        sql.Append(op == "!=" ? $" AND {expr} IS DISTINCT FROM {v}" : $" AND {expr} {op} {v}");
                    }

                    break;
                }
                default:
                    throw new ArgumentException($"Unknown filter operator '{c.Op}' (use <, <=, >, >=, ==, !=, contains, not_contains, exists, not_exists).");
            }
        }

        if (!page) return new Command(sql.ToString(), [.. parameters]);

        var order = query.OrderBy ?? "updated_at";
        var direction = query.Descending ? "DESC" : "ASC";
        var orderBy = order switch
        {
            "updated_at" => "\"UpdatedAt\"",
            "created_at" => "\"CreatedAt\"",
            _ => Field(order).Type is FieldTypes.Integer or FieldTypes.Number ? NumberOf(Literal(order)) : TextOf(Literal(order))
        };
        sql.Append($" ORDER BY {orderBy} {direction} NULLS LAST, \"RecordId\" {direction}");
        sql.Append(" LIMIT ").Append(P(Math.Clamp(query.Limit, 1, MaxLimit))).Append(" OFFSET ").Append(P(Math.Max(0, query.Offset)));
        return new Command(sql.ToString(), [.. parameters]);
    }

    /// <summary>A field name as a SQL literal. Only names that pass the field-name pattern
    /// (lowercase letters, digits, underscores) get here, so there's nothing to escape.</summary>
    private static string Literal(string field) =>
        RecordSchema.IsValidFieldName(field) ? $"'{field}'" : throw new ArgumentException($"'{field}' isn't a valid field name.");

    /// <summary>A field as a number, or null when it holds something else (a record written before
    /// the field's type changed): comparisons then simply don't match it.</summary>
    private static string NumberOf(string field) =>
        $"(CASE WHEN jsonb_typeof(\"DataJson\" -> {field}) = 'number' THEN (\"DataJson\" ->> {field})::numeric END)";

    private static string TextOf(string field) => $"(lower(\"DataJson\" ->> {field}) COLLATE \"C\")";

    /// <summary>The index for an <c>indexed: true</c> field: by install, entity and the field's
    /// value in the same expression queries use. Its name is derived from all three, so creating
    /// it again is a no-op.</summary>
    public static (string Name, string Sql) IndexDdl(string installId, string entity, string field, FieldDefinition def)
    {
        if (!RecordSchema.IsValidFieldName(field) || !RecordSchema.IsValidEntityName(entity)) throw new ArgumentException("Invalid entity or field name.");
        var name = IndexName(installId, entity, field);
        var expr = def.Type is FieldTypes.Integer or FieldTypes.Number ? NumberOf(Literal(field)) : TextOf(Literal(field));
        return (name, $"CREATE INDEX CONCURRENTLY IF NOT EXISTS \"{name}\" ON \"SolutionRecords\" (\"InstallId\", \"Entity\", {expr})");
    }

    public static string IndexName(string installId, string entity, string field) => "ix_rec_" + Contracts.DeterministicId.From("", $"{installId}:{entity}:{field}");
}
