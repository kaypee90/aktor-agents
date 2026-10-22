using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Solutions;
using AgentRuntime.Tools;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>
/// A solution's records as tools (docs/solutions.md#data): <c>list_</c>, <c>get_</c>, <c>create_</c>,
/// <c>update_</c> and <c>delete_</c> for each entity the connection may use, plus
/// <c>describe_data</c>. It's an ordinary connection, so its tools go through the workspace's
/// safety policy, approvals, audit log and watches like any other; the runtime adds it to a
/// solution's workspaces (and to end users' conversations) with a signed binding that fixes which
/// organization, install, end user and entities it reaches.
/// </summary>
public sealed class RecordsPlugin(SolutionTokens tokens, ISolutionCatalog catalog, IRecordStore records) : IToolProviderPlugin
{
    public const string PluginId = "aktor-records";
    public const string BindingKey = "binding";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Solution records",
        Description = "A solution's own data (its entities) as tools. Added by the runtime when a solution is installed.",
        Category = PluginCategory.Data,
        Settings = [new PluginSettingField { Key = BindingKey, Label = "Binding", Secret = true, Required = true, Description = "Set by the runtime." }],
        SetupHelp = "You don't add this one by hand: installing a solution adds it to the solution's workspaces."
    };

    private async Task<(RecordsBinding Binding, InstalledSolution Solution)?> ResolveAsync(PluginConnection connection, CancellationToken ct)
    {
        if (tokens.VerifyBinding(connection.Secrets.GetValueOrDefault(BindingKey)) is not { } binding) return null;
        if (await catalog.GetAsync(binding.TenantId, binding.InstallId, ct) is not { Status: SolutionInstallStatus.Active or SolutionInstallStatus.Installing } solution) return null;
        return (binding, solution);
    }

    public async Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken ct) =>
        await ResolveAsync(connection, ct) is { } r
            ? ConnectionCheck.Success($"{r.Solution.Manifest.DisplayTitle} {r.Solution.Version}: {Accessible(r.Binding, r.Solution).Count()} entities.")
            : ConnectionCheck.Failure("This connection isn't bound to an installed solution. Solutions add it themselves.");

    private static IEnumerable<(string Name, EntityDefinition Definition, EntityAccess Access)> Accessible(RecordsBinding binding, InstalledSolution solution) =>
        solution.Manifest.Entities
            .Select(e => (e.Key, e.Value, EntityAccessNames.Parse(binding.Access.GetValueOrDefault(e.Key))))
            .Where(e => e.Item3 != EntityAccess.None);

    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken ct)
    {
        if (await ResolveAsync(connection, ct) is not { } r) return [];
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "describe_data",
                Description = $"The {r.Solution.Manifest.DisplayTitle} data model you can use: each entity's fields, types and whether you can change it.",
                JsonSchema = """{ "type": "object", "properties": {} }""",
                SideEffects = ToolSideEffects.ReadOnly
            }
        };

        foreach (var (name, def, access) in Accessible(r.Binding, r.Solution))
        {
            var slug = RecordSchema.ToolSlug(name);
            var about = string.IsNullOrWhiteSpace(def.Description) ? name : $"{name} ({def.Description.Trim().TrimEnd('.')})";
            var ownOnly = r.Binding.EndUserId is not null && RecordSchema.EndUserField(def) is not null ? " Only the current user's own records." : string.Empty;
            var fieldNames = string.Join(", ", def.Fields.Where(f => f.Value.Type != FieldTypes.EndUser).Select(f => f.Key));
            tools.Add(new ToolDefinition
            {
                Name = $"list_{slug}",
                Description = $"Find {about} records.{ownOnly} 'where' matches fields exactly; 'filters' compare (<, <=, >, >=, ==, !=, contains, exists), " +
                              $"and dates can be compared with today, today+30d or today-7d. Fields: {fieldNames}.",
                JsonSchema = ListSchema().ToJsonString(),
                SideEffects = ToolSideEffects.ReadOnly
            });
            tools.Add(new ToolDefinition
            {
                Name = $"get_{slug}",
                Description = $"One {name} record by id.{ownOnly}",
                JsonSchema = """{ "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }""",
                SideEffects = ToolSideEffects.ReadOnly
            });
            if (access != EntityAccess.Write) continue;

            tools.Add(new ToolDefinition
            {
                Name = $"create_{slug}",
                Description = $"Create a {about} record. The runtime checks every field against the data model.",
                JsonSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["fields"] = RecordSchema.FieldsSchema(def, forUpdate: false) },
                    ["required"] = new JsonArray("fields")
                }.ToJsonString(),
                // The record id comes from the call's idempotency key: a replay finds the record it made.
                SideEffects = ToolSideEffects.Idempotent
            });
            tools.Add(new ToolDefinition
            {
                Name = $"update_{slug}",
                Description = $"Change fields of a {name} record (only the fields given; null clears one). Pass expected_version from a read to refuse the change if someone changed it since.",
                JsonSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["fields"] = RecordSchema.FieldsSchema(def, forUpdate: true),
                        ["expected_version"] = new JsonObject { ["type"] = "integer" }
                    },
                    ["required"] = new JsonArray("id", "fields")
                }.ToJsonString(),
                SideEffects = ToolSideEffects.Idempotent
            });
            tools.Add(new ToolDefinition
            {
                Name = $"delete_{slug}",
                Description = $"Delete a {name} record.",
                JsonSchema = """{ "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }""",
                SideEffects = ToolSideEffects.Idempotent
            });
        }

        return tools;
    }

    private static JsonObject ListSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["where"] = new JsonObject { ["type"] = "object", ["description"] = "Field → value, all must match.", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
            ["filters"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["field"] = new JsonObject { ["type"] = "string" },
                        ["op"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("<", "<=", ">", ">=", "==", "!=", "contains", "not_contains", "exists", "not_exists") },
                        ["value"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("field", "op")
                }
            },
            ["order_by"] = new JsonObject { ["type"] = "string", ["description"] = $"A field, or created_at / updated_at (default)." },
            ["descending"] = new JsonObject { ["type"] = "boolean" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "At most 200 (default 50)." },
            ["offset"] = new JsonObject { ["type"] = "integer" }
        }
    };

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        var ct = request.CancellationToken;
        if (await ResolveAsync(connection, ct) is not { } r) return ToolExecutionResult.Fail("The solution these records belong to isn't installed any more.", "solution_missing");
        var (binding, solution) = r;
        if (!Tenancy.TenantIds.Same(binding.TenantId, request.TenantId)) return ToolExecutionResult.Fail("These records belong to another organization.", "forbidden");

        var manifest = solution.Manifest;
        if (toolName == "describe_data")
        {
            return ToolExecutionResult.Ok(new JsonObject
            {
                ["solution"] = manifest.DisplayTitle,
                ["version"] = manifest.Version,
                ["entities"] = new JsonArray([.. Accessible(binding, solution).Select(e => (JsonNode)new JsonObject
                {
                    ["name"] = e.Name,
                    ["description"] = e.Definition.Description,
                    ["access"] = e.Access.ToString().ToLowerInvariant(),
                    ["fields"] = new JsonObject(e.Definition.Fields.Where(f => f.Value.Type != FieldTypes.EndUser).Select(f => KeyValuePair.Create(f.Key, (JsonNode?)new JsonObject
                    {
                        ["type"] = f.Value.Type,
                        ["required"] = f.Value.Required,
                        ["values"] = f.Value.Values is { } v ? new JsonArray([.. v.Select(x => (JsonNode)JsonValue.Create(x)!)]) : null,
                        ["entity"] = f.Value.Entity,
                        ["description"] = f.Value.Description
                    })))
                })])
            }.ToJsonString());
        }

        var split = toolName.IndexOf('_');
        if (split <= 0) return ToolExecutionResult.Fail($"Unknown records tool '{toolName}'.");
        var verb = toolName[..split];
        var slug = toolName[(split + 1)..];
        var target = Accessible(binding, solution).FirstOrDefault(e => RecordSchema.ToolSlug(e.Name) == slug);
        if (target.Name is null) return ToolExecutionResult.Fail($"This connection can't use '{slug}' records.", "forbidden");
        if (verb is "create" or "update" or "delete" && target.Access != EntityAccess.Write)
        {
            return ToolExecutionResult.Fail($"This connection can only read {target.Name} records.", "forbidden");
        }

        JsonObject args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return ToolExecutionResult.Fail("The arguments aren't a JSON object.", "bad_arguments");
        }

        var scope = new RecordScope(binding.TenantId, binding.InstallId, binding.EndUserId);
        var writer = new RecordWriter(binding.EndUserId is { } u ? SolutionIds.EndUserActor(binding.InstallId, u) + $" via {request.AgentId}" : request.AgentId, request.TaskId);
        var id = args["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var s) ? s.Trim() : null;
        try
        {
            switch (verb)
            {
                case "list":
                {
                    var query = new RecordQuery
                    {
                        Where = args["where"] is JsonObject w ? w.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => RecordSchema.AsText(p.Value!)) : [],
                        Filters = args["filters"] is JsonArray f ? f.Deserialize<List<WatchCondition>>(ToolJson.Options) ?? [] : [],
                        OrderBy = args["order_by"]?.GetValue<string>(),
                        Descending = args["descending"] is not JsonValue d || !d.TryGetValue<bool>(out var desc) || desc,
                        Limit = args["limit"] is JsonValue l && l.TryGetValue<int>(out var limit) ? limit : 50,
                        Offset = args["offset"] is JsonValue o && o.TryGetValue<int>(out var offset) ? offset : 0
                    };
                    var (items, total) = await records.ListAsync(scope, target.Name, target.Definition, query, ct);
                    return ToolExecutionResult.Ok(new JsonObject
                    {
                        ["entity"] = target.Name,
                        ["total"] = total,
                        ["items"] = new JsonArray([.. items.Select(i => (JsonNode)Visible(i, target.Definition))])
                    }.ToJsonString());
                }
                case "get":
                {
                    if (id is null) return ToolExecutionResult.Fail("'id' is required.", "bad_arguments");
                    return await records.GetAsync(scope, target.Name, target.Definition, id, ct) is { } record
                        ? ToolExecutionResult.Ok(Visible(record, target.Definition).ToJsonString())
                        : ToolExecutionResult.Fail($"No {target.Name} '{id}'.", "not_found");
                }
                case "create":
                {
                    if (args["fields"] is not JsonObject fields) return ToolExecutionResult.Fail("'fields' is required.", "bad_arguments");
                    var recordId = string.IsNullOrEmpty(request.IdempotencyKey) ? null : SolutionIds.RecordFor($"{binding.InstallId}:{request.IdempotencyKey}");
                    return Result(await records.CreateAsync(scope, manifest, target.Name, fields, writer, recordId, ct), target.Definition, "created");
                }
                case "update":
                {
                    if (id is null || args["fields"] is not JsonObject fields) return ToolExecutionResult.Fail("'id' and 'fields' are required.", "bad_arguments");
                    int? expected = args["expected_version"] is JsonValue ev && ev.TryGetValue<int>(out var v) ? v : null;
                    return Result(await records.UpdateAsync(scope, manifest, target.Name, id, fields, writer, expected, ct), target.Definition, "updated");
                }
                case "delete":
                {
                    if (id is null) return ToolExecutionResult.Fail("'id' is required.", "bad_arguments");
                    var deleted = await records.DeleteAsync(scope, manifest, target.Name, id, writer, ct);
                    // Deleting what's already gone (a replay) is done, not an error.
                    if (deleted.NotFound) return ToolExecutionResult.Ok(new JsonObject { ["deleted"] = id, ["note"] = "It doesn't exist (any more)." }.ToJsonString());
                    return Result(deleted, target.Definition, "deleted");
                }
                default:
                    return ToolExecutionResult.Fail($"Unknown records tool '{toolName}'.");
            }
        }
        catch (ArgumentException ex)
        {
            return ToolExecutionResult.Fail(ex.Message, "bad_arguments");
        }
        catch (InvalidOperationException ex)
        {
            return ToolExecutionResult.Fail(ex.Message, "bad_arguments");
        }
    }

    /// <summary>What an agent sees of a record: everything but who owns it.</summary>
    private static JsonObject Visible(SolutionRecord record, EntityDefinition def)
    {
        var json = record.ToJson();
        if (RecordSchema.EndUserField(def) is { } owner) json.Remove(owner);
        return json;
    }

    private static ToolExecutionResult Result(RecordWriteResult result, EntityDefinition def, string done)
    {
        if (result.Success) return ToolExecutionResult.Ok(new JsonObject { [done] = Visible(result.Record!, def) }.ToJsonString());
        var code = result.NotFound ? "not_found" : result.Conflict ? "conflict" : "invalid";
        return ToolExecutionResult.Fail(string.Join(" ", result.Errors), code, new JsonObject { ["errors"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)JsonValue.Create(e)!)]) }.ToJsonString());
    }
}
