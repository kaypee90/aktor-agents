using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Secrets;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Integrations;
using AgentRuntime.Memory;
using AgentRuntime.Skills;
using AgentRuntime.Solutions;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

public sealed class SolutionException(string message, int status = StatusCodes.Status400BadRequest, IReadOnlyList<string>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public IReadOnlyList<string> Errors { get; } = errors ?? [message];
}

/// <summary>An install or upgrade: the package, input values, and the secrets of its connectors
/// (connector name → secret field → value). Secrets go straight to the vault.</summary>
public sealed record SolutionDeployRequest
{
    public SolutionBundle Bundle { get; init; } = new();
    /// <summary>Install from the registry instead of a bundle: "name", "name@1.2.0" or "name@latest".</summary>
    public string? Package { get; init; }
    public Dictionary<string, string>? Inputs { get; init; }
    public Dictionary<string, Dictionary<string, string>>? Secrets { get; init; }
    /// <summary>Connectors to back with the organization's global connectors (connector name →
    /// global connector name or id) instead of secrets; "" goes back to asking for secrets. Kept
    /// across upgrades until changed.</summary>
    public Dictionary<string, string>? Globals { get; init; }
    /// <summary>Upgrades only: allow the same or a lower version (for a development loop).</summary>
    public bool Force { get; init; }
    /// <summary>Upgrades only: check and report what would change, without changing anything.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Installs, upgrades and uninstalls solutions (docs/solutions.md). Everything a solution makes is
/// made through the same paths as by hand (workspaces, connections, triggers, skills, knowledge),
/// so the same plan quotas, validation, audit log and tenant isolation apply.
/// </summary>
public sealed class SolutionService(
    IGrainFactory grains,
    IDbContextFactory<AgentDbContext> dbFactory,
    WorkspaceCopyService copies,
    PluginCatalog plugins,
    ISolutionCatalog catalog,
    IRecordStore records,
    SolutionTokens tokens,
    SecretProtector protector,
    ISkillStore skills,
    IMemoryStore memory,
    ISecretStore secretStore,
    IGlobalConnectorStore globalConnectors,
    IOptions<Pipelines.PipelineOptions> pipelineOptions,
    TimeProvider clock,
    ILogger<SolutionService> logger)
{
    public const string SeedWriter = "solution:seed";

    /// <summary>Where the secrets of a connector the assistant uses are kept (the vault), so an end
    /// user's conversation can connect it even if no workspace of the solution has it.</summary>
    public static string AssistantSecretScope(string installId, string connector) => $"solution:{installId}/assistant/{connector}";

    /// <summary>Saves the secrets given for the assistant's connectors; true for each that now has them.</summary>
    private async Task<HashSet<string>> StoreAssistantSecretsAsync(string installId, SolutionManifest manifest,
        Dictionary<string, Dictionary<string, string>>? secrets, CancellationToken ct)
    {
        var stored = new HashSet<string>();
        foreach (var name in manifest.Assistant?.Connectors ?? [])
        {
            if (manifest.Connectors.FirstOrDefault(c => c.Name == name)?.Global is not null) continue;
            if (secrets?.GetValueOrDefault(name) is not { Count: > 0 } values) continue;
            foreach (var (key, value) in values.Where(v => !string.IsNullOrEmpty(v.Value)))
            {
                await secretStore.PutAsync(AssistantSecretScope(installId, name), key, value, ct);
            }

            stored.Add(name);
        }

        return stored;
    }

    /// <summary>Assistant connectors whose required secrets are already in the vault.</summary>
    private async Task<HashSet<string>> AssistantConnectedAsync(string installId, SolutionManifest manifest, CancellationToken ct)
    {
        var connected = new HashSet<string>();
        foreach (var name in manifest.Assistant?.Connectors ?? [])
        {
            if (manifest.Connectors.FirstOrDefault(c => c.Name == name) is not { } c) continue;
            if (c.Global is not null)
            {
                connected.Add(name);
                continue;
            }

            var required = plugins.Get(c.Plugin)?.Manifest.Settings.Where(f => f.Secret && f.Required).Select(f => f.Key).ToList() ?? [];
            var all = true;
            foreach (var key in required) all &= await secretStore.GetAsync(AssistantSecretScope(installId, name), key, ct) is { Length: > 0 };
            if (all) connected.Add(name);
        }

        return connected;
    }

    private static IEnumerable<(string Entity, string Field, FieldDefinition Def)> IndexedFields(SolutionManifest m) =>
        m.Entities.SelectMany(e => e.Value.Fields.Where(f => f.Value.Indexed).Select(f => (e.Key, f.Key, f.Value)));

    /// <summary>
    /// Creates the indexes for <c>indexed: true</c> fields, and drops those of fields that no
    /// longer ask for one. Built concurrently, so writes carry on while a large table is indexed.
    /// </summary>
    private async Task SyncIndexesAsync(string installId, SolutionManifest manifest, SolutionManifest? previous, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        var wanted = IndexedFields(manifest).ToList();
        foreach (var (entity, field, def) in wanted)
        {
            var (_, sql) = RecordQuerySql.IndexDdl(installId, entity, field, def);
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }

        var keep = wanted.Select(w => RecordQuerySql.IndexName(installId, w.Entity, w.Field)).ToHashSet();
        foreach (var (entity, field, _) in previous is null ? Enumerable.Empty<(string, string, FieldDefinition)>() : IndexedFields(previous))
        {
            var name = RecordQuerySql.IndexName(installId, entity, field);
            // The type may have changed (text ↔ number): the expression differs, so rebuild it.
            var changed = wanted.Any(w => w.Entity == entity && w.Field == field) &&
                          previous!.Entities[entity].Fields[field].Type != manifest.Entities[entity].Fields[field].Type;
            if (keep.Contains(name) && !changed) continue;
            await db.Database.ExecuteSqlRawAsync($"DROP INDEX CONCURRENTLY IF EXISTS \"{name}\"", ct);
            if (changed)
            {
                var (_, sql) = RecordQuerySql.IndexDdl(installId, entity, field, manifest.Entities[entity].Fields[field]);
                await db.Database.ExecuteSqlRawAsync(sql, ct);
            }
        }
    }

    /// <summary>Who knowledge a solution installs is recorded as written by.</summary>
    public static string KnowledgeAuthor(string installId) => $"solution:{installId}";

    // ---------------------------------------------------------------- reading packages

    public sealed record Checked(SolutionManifest? Manifest, SolutionValidation Validation, Dictionary<string, string> Files);

    public Checked Check(SolutionBundle bundle)
    {
        var (manifest, readErrors) = SolutionManifestReader.Read(bundle);
        var files = SolutionManifestReader.Files(bundle);
        if (manifest is null) return new Checked(null, new SolutionValidation { Errors = readErrors }, files);
        var validation = SolutionValidator.Validate(manifest, id => plugins.Get(id) is not null, pipelineOptions.Value, files);
        foreach (var skill in manifest.Skills.Where(s => files.ContainsKey(s.Path)))
        {
            try
            {
                SkillPackage.FromMarkdown(files[skill.Path]);
            }
            catch (SkillPackageException ex)
            {
                validation.Errors.Add($"Skill '{skill.Path}': {ex.Message}");
            }
        }

        return new Checked(manifest, validation, files);
    }

    /// <summary>The secret fields each connector needs (from its plugin), for the install form.</summary>
    public Dictionary<string, List<object>> SecretsNeeded(SolutionManifest manifest) =>
        manifest.Connectors.ToDictionary(c => c.Name, c => (plugins.Get(c.Plugin)?.Manifest.Settings ?? [])
            .Where(f => f.Secret)
            .Select(f => (object)new { key = f.Key, label = f.Label, required = f.Required, description = f.Description })
            .ToList());

    public async Task<object> ValidateAsync(string tenantId, SolutionBundle bundle, CancellationToken ct)
    {
        var c = Check(bundle);
        var available = await globalConnectors.ListAsync(tenantId, ct);
        var warnings = new List<string>(c.Validation.Warnings);
        foreach (var connector in c.Manifest?.Connectors.Where(x => x.Global is not null) ?? [])
        {
            var match = available.FirstOrDefault(g => string.Equals(g.Name, connector.Global, StringComparison.OrdinalIgnoreCase));
            if (match is null) warnings.Add($"Connector '{connector.Name}': this organization has no global connector '{connector.Global}'; pick one or enter secrets at deploy.");
            else if (match.PluginId != connector.Plugin) warnings.Add($"Connector '{connector.Name}': the global connector '{connector.Global}' is {match.PluginId}, not {connector.Plugin}.");
        }

        return new
        {
            ok = c.Validation.Ok,
            errors = c.Validation.Errors,
            warnings,
            // The organization's global connectors each connector could use instead of its own secrets.
            globals = c.Manifest?.Connectors.ToDictionary(x => x.Name, x => available.Where(g => g.PluginId == x.Plugin)
                .Select(g => new { connector_id = g.ConnectorId, name = g.Name, description = g.Description }).ToList()),
            summary = c.Manifest is null ? null : SolutionValidator.Summarize(c.Manifest),
            secrets = c.Manifest is null ? null : SecretsNeeded(c.Manifest),
            setup_help = c.Manifest?.Connectors.ToDictionary(x => x.Name, x => plugins.Get(x.Plugin)?.Manifest.SetupHelp)
        };
    }

    private Checked CheckOrThrow(SolutionBundle bundle)
    {
        var c = Check(bundle);
        if (!c.Validation.Ok || c.Manifest is null) throw new SolutionException("The package isn't valid.", StatusCodes.Status400BadRequest, c.Validation.Errors);
        return c;
    }

    // ---------------------------------------------------------------- install

    public async Task<JsonObject> InstallAsync(string tenantId, string by, SolutionDeployRequest request, CancellationToken ct)
    {
        var (checkedManifest, validation, files) = CheckOrThrow(request.Bundle);
        var (manifest, globalErrors) = await BindGlobalsAsync(tenantId, checkedManifest!, request.Globals, previous: null, ct);
        var (inputs, inputErrors) = SolutionValidator.ResolveInputs(manifest, request.Inputs);
        var secretErrors = MissingSecrets(manifest, request.Secrets, alreadyConnected: [], assistantConnected: []);
        if (inputErrors.Count + secretErrors.Count + globalErrors.Count > 0)
        {
            throw new SolutionException("Some install values are missing or wrong.", 400, [.. globalErrors, .. inputErrors, .. secretErrors]);
        }
        manifest = Resolve(manifest, inputs);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.SolutionInstalls.AnyAsync(s => s.TenantId == tenantId && s.Name == manifest.Name && s.Status != nameof(SolutionInstallStatus.Uninstalled), ct))
            {
                throw new SolutionException($"'{manifest.Name}' is already installed here; upgrade it instead.", StatusCodes.Status409Conflict);
            }
        }

        var installId = SolutionIds.NewInstall();
        var now = clock.GetUtcNow();
        var row = new SolutionInstallRecord
        {
            InstallId = installId,
            TenantId = tenantId,
            Name = manifest.Name,
            Title = manifest.DisplayTitle,
            Version = manifest.Version,
            Status = nameof(SolutionInstallStatus.Installing),
            BundleJson = JsonSerializer.Serialize(request.Bundle, SolutionManifestReader.Json),
            ManifestJson = JsonSerializer.Serialize(manifest, SolutionManifestReader.Json),
            InputsJson = JsonSerializer.Serialize(inputs),
            InstalledBy = by,
            InstalledAt = now,
            UpdatedAt = now,
            HistoryJson = JsonSerializer.Serialize(new[] { new { version = manifest.Version, at = now, by, action = "install" } })
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.SolutionInstalls.Add(row);
            await db.SaveChangesAsync(ct);
        }

        catalog.Invalidate(installId);
        var workspaces = new Dictionary<string, string>();
        var report = new JsonArray();
        try
        {
            var seeded = await SeedAsync(tenantId, installId, manifest, ct);
            await StoreAssistantSecretsAsync(installId, manifest, request.Secrets, ct);

            foreach (var w in manifest.Workspaces)
            {
                var (workspaceId, result) = await CreateWorkspaceAsync(tenantId, by, installId, manifest, w, inputs, request.Secrets, ct);
                workspaces[w.Key] = workspaceId;
                report.Add(result);
            }

            await InstallKnowhowAsync(tenantId, installId, manifest, files, workspaces, previous: null, by, ct);
            await SyncIndexesAsync(installId, manifest, previous: null, ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var saved = await db.SolutionInstalls.FirstAsync(s => s.InstallId == installId, ct);
            saved.WorkspacesJson = JsonSerializer.Serialize(workspaces);
            saved.Status = nameof(SolutionInstallStatus.Active);
            saved.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            catalog.Invalidate(installId);
            logger.LogInformation("Installed solution {Name} {Version} as {InstallId} for {TenantId} ({Workspaces} workspaces, {Seeded} seed records)",
                manifest.Name, manifest.Version, installId, tenantId, workspaces.Count, seeded);

            return new JsonObject
            {
                ["install_id"] = installId,
                ["name"] = manifest.Name,
                ["version"] = manifest.Version,
                ["status"] = "Active",
                ["workspaces"] = report,
                ["seeded_records"] = seeded,
                ["warnings"] = new JsonArray([.. validation.Warnings.Select(x => (JsonNode)JsonValue.Create(x)!)]),
                ["webhook_secret"] = manifest.Webhooks.Count > 0 ? RecordChangeDispatcher.WebhookSecret(protector, installId) : null
            };
        }
        catch (Exception ex)
        {
            // Nothing half-installed: what was made is archived and the install marked removed.
            logger.LogWarning(ex, "Installing {Name} failed; rolling back {Count} workspace(s)", manifest.Name, workspaces.Count);
            foreach (var id in workspaces.Values) await grains.GetGrain<IWorkspaceGrain>(id).Archive();
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.SolutionInstalls.Where(s => s.InstallId == installId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, nameof(SolutionInstallStatus.Uninstalled)).SetProperty(s => s.WorkspacesJson, JsonSerializer.Serialize(workspaces)), CancellationToken.None);
            await db.SolutionRecords.Where(r => r.InstallId == installId).ExecuteDeleteAsync(CancellationToken.None);
            await db.SolutionRecordChanges.Where(r => r.InstallId == installId).ExecuteDeleteAsync(CancellationToken.None);
            catalog.Invalidate(installId);
            if (ex is SolutionException or WorkspaceCopyException) throw new SolutionException($"Install failed: {ex.Message}", ex is WorkspaceCopyException w ? w.Status : 400);
            throw;
        }
    }

    /// <summary>
    /// Which connectors use a global connector: the deploy's choices, else what the package says,
    /// else what the install used before (so an upgrade keeps them). Each must exist in this
    /// organization and be the same plugin.
    /// </summary>
    private async Task<(SolutionManifest Manifest, List<string> Errors)> BindGlobalsAsync(string tenantId, SolutionManifest manifest,
        Dictionary<string, string>? chosen, SolutionManifest? previous, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var name in chosen?.Keys.Where(k => manifest.Connectors.All(c => c.Name != k)) ?? []) errors.Add($"'{name}' isn't a connector of this solution.");
        var bound = new List<SolutionConnector>();
        foreach (var c in manifest.Connectors)
        {
            var global = chosen is not null && chosen.TryGetValue(c.Name, out var pick)
                ? pick
                : c.Global ?? previous?.Connectors.FirstOrDefault(p => p.Name == c.Name && p.Plugin == c.Plugin)?.Global;
            if (string.IsNullOrWhiteSpace(global))
            {
                bound.Add(c with { Global = null });
                continue;
            }

            var found = await globalConnectors.GetAsync(tenantId, global.Trim(), ct);
            if (found is null) errors.Add($"Connector '{c.Name}': there's no global connector '{global}' in this organization.");
            else if (found.PluginId != c.Plugin) errors.Add($"Connector '{c.Name}' needs {c.Plugin}, but the global connector '{found.Name}' is {found.PluginId}.");
            bound.Add(c with { Global = found?.Name ?? global });
        }

        return (manifest with { Connectors = bound }, errors);
    }

    /// <summary>The manifest with its inputs filled in where the runtime reads them (connector
    /// settings, webhook URLs, the assistant's instructions and greeting); workspaces see the
    /// inputs as variables.</summary>
    private static SolutionManifest Resolve(SolutionManifest manifest, IReadOnlyDictionary<string, string> inputs) => manifest with
    {
        Connectors = manifest.Connectors.Select(c => c with
        {
            Settings = c.Settings.ToDictionary(kv => kv.Key, kv => SolutionValidator.ReplaceInputs(kv.Value, inputs))
        }).ToList(),
        Webhooks = manifest.Webhooks.Select(h => h with { Url = SolutionValidator.ReplaceInputs(h.Url, inputs) }).ToList(),
        EndUsers = manifest.EndUsers?.Jwt is { } jwt ? manifest.EndUsers with
        {
            Jwt = jwt with
            {
                Issuer = SolutionValidator.ReplaceInputs(jwt.Issuer, inputs),
                Audience = SolutionValidator.ReplaceInputs(jwt.Audience, inputs),
                JwksUrl = SolutionValidator.ReplaceInputs(jwt.JwksUrl, inputs)
            }
        } : manifest.EndUsers,
        Assistant = manifest.Assistant is { } a ? a with
        {
            Instructions = SolutionValidator.ReplaceInputs(a.Instructions, inputs),
            Greeting = a.Greeting is null ? null : SolutionValidator.ReplaceInputs(a.Greeting, inputs)
        } : null
    };

    /// <summary>Secrets missing for required connectors: those a workspace uses and doesn't have
    /// yet, and those the assistant uses that aren't in its vault entry yet.</summary>
    private List<string> MissingSecrets(SolutionManifest manifest, Dictionary<string, Dictionary<string, string>>? given, HashSet<string> alreadyConnected,
        HashSet<string> assistantConnected)
    {
        var errors = new List<string>();
        foreach (var c in manifest.Connectors.Where(c => c.Required && c.Global is null))
        {
            var workspaceNeeds = manifest.Workspaces.Any(w => w.Connectors.Contains(c.Name)) && !alreadyConnected.Contains(c.Name);
            var assistantNeeds = manifest.Assistant?.Connectors.Contains(c.Name) == true && !assistantConnected.Contains(c.Name);
            if (!workspaceNeeds && !assistantNeeds) continue;
            foreach (var field in plugins.Get(c.Plugin)?.Manifest.Settings.Where(f => f.Secret && f.Required) ?? [])
            {
                if (given?.GetValueOrDefault(c.Name)?.GetValueOrDefault(field.Key) is not { Length: > 0 }) errors.Add($"Connector '{c.Name}' needs its '{field.Key}' secret ({field.Label}).");
            }
        }

        foreach (var name in given?.Keys.Where(k => manifest.Connectors.All(c => c.Name != k)) ?? []) errors.Add($"'{name}' isn't a connector of this solution.");
        return errors;
    }

    private ConnectionRequest DataConnection(string tenantId, string installId, Dictionary<string, string> access, string? endUserId = null) => new()
    {
        PluginId = RecordsPlugin.PluginId,
        Name = SolutionValidator.DataConnectionName,
        Secrets = new Dictionary<string, string> { [RecordsPlugin.BindingKey] = tokens.SignBinding(new RecordsBinding(tenantId, installId, endUserId, access)) }
    };

    public ConnectionRequest EndUserDataConnection(string tenantId, string installId, SolutionAssistant assistant, string endUserId) =>
        DataConnection(tenantId, installId, assistant.Data, endUserId);

    private static ConnectionRequest ConnectorConnection(SolutionConnector c, Dictionary<string, Dictionary<string, string>>? secrets) => c.Global is { } global
        ? new() { PluginId = c.Plugin, Name = c.Name, GlobalConnector = global }
        : new()
        {
            PluginId = c.Plugin,
            Name = c.Name,
            Settings = new Dictionary<string, string>(c.Settings),
            Secrets = secrets?.GetValueOrDefault(c.Name) is { } s ? new Dictionary<string, string>(s) : []
        };

    private static Dictionary<string, string> Variables(SolutionWorkspace w, IReadOnlyDictionary<string, string> inputs)
    {
        var vars = new Dictionary<string, string>(w.Variables);
        foreach (var (k, v) in inputs) vars[k] = v;
        return vars;
    }

    private static string WorkspaceName(SolutionManifest m, SolutionWorkspace w) => Clip($"{m.DisplayTitle} · {w.Name}", 100);

    private async Task<(string WorkspaceId, JsonObject Report)> CreateWorkspaceAsync(string tenantId, string by, string installId, SolutionManifest manifest,
        SolutionWorkspace w, IReadOnlyDictionary<string, string> inputs, Dictionary<string, Dictionary<string, string>>? secrets, CancellationToken ct)
    {
        var connections = new List<ConnectionRequest>();
        if (w.Data.Count > 0) connections.Add(DataConnection(tenantId, installId, w.Data));
        foreach (var name in w.Connectors)
        {
            if (manifest.Connectors.FirstOrDefault(c => c.Name == name) is { } c && plugins.Get(c.Plugin) is not null) connections.Add(ConnectorConnection(c, secrets));
        }

        var definition = new WorkspaceDefinition
        {
            Name = WorkspaceName(manifest, w),
            Goal = w.Goal,
            Pipeline = w.Pipeline,
            SafetyPolicy = w.Safety ?? new Safety.WorkspaceSafetyPolicy(),
            DailyTokenLimit = w.DailyTokens ?? 0,
            DailyCostLimitUsd = w.DailyCostUsd ?? 0,
            Triggers = w.Triggers,
            Connections = connections,
            Variables = Variables(w, inputs)
        };
        var (id, connectionResults, triggerResults) = await copies.CreateFromDefinitionAsync(tenantId, by, definition, definition.Name,
            templateId: $"solution:{manifest.Name}", addConnections: true, ct);
        var report = new JsonObject
        {
            ["key"] = w.Key,
            ["workspace_id"] = id,
            ["name"] = definition.Name,
            ["connections"] = JsonSerializer.SerializeToNode(connectionResults),
            ["triggers"] = JsonSerializer.SerializeToNode(triggerResults)
        };
        return (id, report);
    }

    private async Task<int> SeedAsync(string tenantId, string installId, SolutionManifest manifest, CancellationToken ct)
    {
        if (manifest.Seed.Count == 0) return 0;
        var scope = new RecordScope(tenantId, installId);
        var writer = new RecordWriter(SeedWriter);

        // Seed records may name each other by their own ids ("id: p-1"); those become real ids, and
        // entities are seeded after the ones they refer to.
        var ids = new Dictionary<string, string>();
        foreach (var (entity, list) in manifest.Seed)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var own = list[i]["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : $"{entity}#{i}";
                ids[own] = SolutionIds.RecordFor($"{installId}:seed:{entity}:{own}");
            }
        }

        var order = new List<string>();
        var pending = manifest.Seed.Keys.ToList();
        for (var pass = 0; pass < manifest.Entities.Count + 1 && pending.Count > 0; pass++)
        {
            foreach (var entity in pending.ToList())
            {
                var refs = manifest.Entities[entity].Fields.Values.Where(f => f.Type == FieldTypes.Ref).Select(f => f.Entity!).Where(e => e != entity);
                if (refs.All(r => !pending.Contains(r) || order.Contains(r)))
                {
                    order.Add(entity);
                    pending.Remove(entity);
                }
            }
        }

        order.AddRange(pending);
        var count = 0;
        foreach (var entity in order)
        {
            var def = manifest.Entities[entity];
            var list = manifest.Seed[entity];
            for (var i = 0; i < list.Count; i++)
            {
                var data = (JsonObject)list[i].DeepClone();
                var own = data["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : $"{entity}#{i}";
                data.Remove("id");
                foreach (var (field, _) in def.Fields.Where(f => f.Value.Type == FieldTypes.Ref))
                {
                    if (data[field] is JsonValue r && r.TryGetValue<string>(out var target) && ids.TryGetValue(target, out var real)) data[field] = real;
                }

                var created = await records.CreateAsync(scope, manifest, entity, data, writer, ids[own], ct);
                if (!created.Success) throw new SolutionException($"Seed {entity} '{own}': {string.Join(" ", created.Errors)}");
                count++;
            }
        }

        // Seed data isn't news: no triggers or webhooks for it.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SolutionRecordChanges.Where(c => c.InstallId == installId && c.ChangedBy == SeedWriter && c.DispatchedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.DispatchedAt, clock.GetUtcNow()), ct);
        return count;
    }

    /// <summary>Skills and knowledge for each workspace; on an upgrade, the previous version's
    /// that are gone are removed.</summary>
    private async Task InstallKnowhowAsync(string tenantId, string installId, SolutionManifest manifest, IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> workspaces, (SolutionManifest Manifest, Dictionary<string, string> Files)? previous, string by, CancellationToken ct)
    {
        IEnumerable<string> Targets(string? key) => key is null ? workspaces.Values : workspaces.TryGetValue(key, out var id) ? [id] : [];

        var wantedSkills = new HashSet<(string Workspace, string Name)>();
        foreach (var s in manifest.Skills)
        {
            var doc = SkillPackage.FromMarkdown(files[s.Path]);
            foreach (var workspaceId in Targets(s.Workspace))
            {
                wantedSkills.Add((workspaceId, doc.Name));
                var existing = await skills.GetAsync(tenantId, doc.Name, workspaceId, ct);
                if (existing is not null && existing.Instructions == doc.Instructions && existing.Description == doc.Description) continue;
                await skills.SaveAsync(tenantId, doc with { WorkspaceId = workspaceId, UpdatedBy = by, UpdatedAt = clock.GetUtcNow() }, workspaceId, ct);
            }
        }

        var author = KnowledgeAuthor(installId);
        var wantedKnowledge = new HashSet<(string Workspace, string Key)>();
        foreach (var k in manifest.Knowledge)
        {
            var text = k.Path is { } path ? files[path] : k.Text!;
            foreach (var workspaceId in Targets(k.Workspace))
            {
                wantedKnowledge.Add((workspaceId, k.Key));
                await memory.WriteAsync(new MemoryRecord
                {
                    TenantId = tenantId,
                    AgentId = author,
                    Kind = Contracts.MemoryKind.Shared,
                    Key = k.Key,
                    Value = text.Length > 100_000 ? text[..100_000] : text,
                    WorkspaceId = workspaceId
                }, ct);
            }
        }

        if (previous is not { } old) return;
        foreach (var s in old.Manifest.Skills.Where(s => old.Files.ContainsKey(s.Path)))
        {
            SkillDocument doc;
            try
            {
                doc = SkillPackage.FromMarkdown(old.Files[s.Path]);
            }
            catch (SkillPackageException)
            {
                continue;
            }

            foreach (var workspaceId in Targets(s.Workspace).Where(id => !wantedSkills.Contains((id, doc.Name))))
            {
                await skills.DeleteAsync(tenantId, doc.Name, workspaceId, ct);
            }
        }

        foreach (var k in old.Manifest.Knowledge)
        {
            foreach (var workspaceId in Targets(k.Workspace).Where(id => !wantedKnowledge.Contains((id, k.Key))))
            {
                var scope = MemoryScope.OnlyWorkspace(workspaceId);
                if (await memory.ReadAsync(tenantId, author, k.Key, scope, ct) is { } entry && entry.AgentId == author)
                {
                    await memory.DeleteSharedAsync(tenantId, scope, memoryIds: [entry.MemoryId], cancellationToken: ct);
                }
            }
        }
    }

    // ---------------------------------------------------------------- upgrade

    public async Task<JsonObject> UpgradeAsync(string tenantId, string installId, string by, SolutionDeployRequest request, CancellationToken ct)
    {
        var row = await RowAsync(tenantId, installId, ct);
        if (row.Status != nameof(SolutionInstallStatus.Active)) throw new SolutionException("Only an active install can be upgraded.", StatusCodes.Status409Conflict);
        var (checkedManifest, validation, files) = CheckOrThrow(request.Bundle);
        if (checkedManifest!.Name != row.Name) throw new SolutionException($"This package is '{checkedManifest.Name}', but the install is '{row.Name}'.", StatusCodes.Status409Conflict);
        var old = EfSolutionCatalog.ToSolution(row).Manifest;
        var (manifest, globalErrors) = await BindGlobalsAsync(tenantId, checkedManifest, request.Globals, old, ct);
        var order = SolutionValidator.CompareVersions(manifest.Version, row.Version);
        if (order <= 0 && !request.Force)
        {
            throw new SolutionException(order == 0
                ? $"{row.Version} is already installed; bump the version (or force it, e.g. while developing)."
                : $"{manifest.Version} is older than the installed {row.Version}; force it to go back.", StatusCodes.Status409Conflict);
        }

        var previousInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(row.InputsJson) ?? [];
        var (inputs, inputErrors) = SolutionValidator.ResolveInputs(manifest, request.Inputs, previousInputs);
        var workspaces = JsonSerializer.Deserialize<Dictionary<string, string>>(row.WorkspacesJson) ?? [];
        var oldBundle = JsonSerializer.Deserialize<SolutionBundle>(row.BundleJson, SolutionManifestReader.Json) ?? new SolutionBundle();

        // Connectors a workspace already has don't need their secrets again, unless they're moving
        // between a global connector and their own secrets.
        var connected = new HashSet<string>();
        foreach (var w in manifest.Workspaces.Where(w => workspaces.ContainsKey(w.Key)))
        {
            foreach (var c in await grains.GetGrain<IWorkspaceGrain>(workspaces[w.Key]).ListConnections())
            {
                var wanted = manifest.Connectors.FirstOrDefault(x => x.Name == c.Name);
                if (wanted is null || SameGlobal(wanted.Global, c.GlobalConnectorName)) connected.Add(c.Name);
            }
        }

        var assistantConnected = await AssistantConnectedAsync(installId, manifest, ct);
        var newConnectors = manifest.Connectors.Where(c => !connected.Contains(c.Name)).Select(c => c.Name).ToHashSet();
        var secretErrors = MissingSecrets(manifest, request.Secrets, connected, assistantConnected);
        if (inputErrors.Count + secretErrors.Count + globalErrors.Count > 0)
        {
            throw new SolutionException("Some values the new version needs are missing or wrong.", 400, [.. globalErrors, .. inputErrors, .. secretErrors]);
        }
        manifest = Resolve(manifest, inputs);

        var plan = new JsonObject
        {
            ["from"] = row.Version,
            ["to"] = manifest.Version,
            ["added_workspaces"] = new JsonArray([.. manifest.Workspaces.Where(w => !workspaces.ContainsKey(w.Key)).Select(w => (JsonNode)JsonValue.Create(w.Key)!)]),
            ["removed_workspaces"] = new JsonArray([.. workspaces.Keys.Where(k => manifest.Workspaces.All(w => w.Key != k)).Select(k => (JsonNode)JsonValue.Create(k)!)]),
            ["changed_workspaces"] = new JsonArray([.. manifest.Workspaces.Where(w => workspaces.ContainsKey(w.Key)).Select(w => (JsonNode)JsonValue.Create(w.Key)!)]),
            ["added_entities"] = new JsonArray([.. manifest.Entities.Keys.Except(old.Entities.Keys).Select(k => (JsonNode)JsonValue.Create(k)!)]),
            ["removed_entities"] = new JsonArray([.. old.Entities.Keys.Except(manifest.Entities.Keys).Select(k => (JsonNode)JsonValue.Create(k)!)]),
            ["new_connectors"] = new JsonArray([.. newConnectors.Select(k => (JsonNode)JsonValue.Create(k)!)]),
            ["warnings"] = new JsonArray([.. validation.Warnings.Concat(SchemaWarnings(old, manifest)).Select(x => (JsonNode)JsonValue.Create(x)!)])
        };
        if (request.DryRun) return new JsonObject { ["install_id"] = installId, ["dry_run"] = true, ["plan"] = plan };
        await StoreAssistantSecretsAsync(installId, manifest, request.Secrets, ct);

        var report = new JsonArray();
        foreach (var w in manifest.Workspaces)
        {
            if (!workspaces.TryGetValue(w.Key, out var workspaceId))
            {
                var (id, created) = await CreateWorkspaceAsync(tenantId, by, installId, manifest, w, inputs, request.Secrets, ct);
                workspaces[w.Key] = id;
                created["action"] = "created";
                report.Add(created);
                continue;
            }

            report.Add(await UpgradeWorkspaceAsync(tenantId, by, installId, manifest, old, w, workspaceId, inputs, request.Secrets, ct));
        }

        foreach (var key in workspaces.Keys.Where(k => manifest.Workspaces.All(w => w.Key != k)).ToList())
        {
            await grains.GetGrain<IWorkspaceGrain>(workspaces[key]).Archive();
            report.Add(new JsonObject { ["key"] = key, ["workspace_id"] = workspaces[key], ["action"] = "archived" });
            workspaces.Remove(key);
        }

        await InstallKnowhowAsync(tenantId, installId, manifest, files, workspaces, (old, SolutionManifestReader.Files(oldBundle)), by, ct);
        await SyncIndexesAsync(installId, manifest, old, ct);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var saved = await db.SolutionInstalls.FirstAsync(s => s.InstallId == installId, ct);
            var history = JsonNode.Parse(saved.HistoryJson) as JsonArray ?? [];
            history.Add(new JsonObject { ["version"] = manifest.Version, ["at"] = clock.GetUtcNow(), ["by"] = by, ["action"] = request.Force && order <= 0 ? "redeploy" : "upgrade", ["from"] = row.Version });
            saved.Version = manifest.Version;
            saved.Title = manifest.DisplayTitle;
            saved.BundleJson = JsonSerializer.Serialize(request.Bundle, SolutionManifestReader.Json);
            saved.ManifestJson = JsonSerializer.Serialize(manifest, SolutionManifestReader.Json);
            saved.InputsJson = JsonSerializer.Serialize(inputs);
            saved.WorkspacesJson = JsonSerializer.Serialize(workspaces);
            saved.HistoryJson = history.ToJsonString();
            saved.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        catalog.Invalidate(installId);
        logger.LogInformation("Upgraded solution {Name} ({InstallId}) from {From} to {To}", manifest.Name, installId, row.Version, manifest.Version);
        return new JsonObject { ["install_id"] = installId, ["name"] = manifest.Name, ["version"] = manifest.Version, ["plan"] = plan, ["workspaces"] = report };
    }

    private static bool SameGlobal(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool PipelineDiffers(Pipelines.PipelineDefinition wanted, Pipelines.PipelineDefinition current) =>
        JsonSerializer.Serialize(wanted.Stages) != JsonSerializer.Serialize(current.Stages) ||
        wanted.MaxConcurrentRuns != current.MaxConcurrentRuns || wanted.MaxRunMinutes != current.MaxRunMinutes || wanted.ResultUrgency != current.ResultUrgency;

    /// <summary>What an entity change means for records already stored (they aren't rewritten).</summary>
    private static IEnumerable<string> SchemaWarnings(SolutionManifest old, SolutionManifest now)
    {
        foreach (var (name, entity) in now.Entities)
        {
            if (!old.Entities.TryGetValue(name, out var before)) continue;
            foreach (var (field, def) in entity.Fields)
            {
                if (!before.Fields.TryGetValue(field, out var was))
                {
                    if (def.Required) yield return $"{name}.{field} is new and required: existing records don't have it, and must get it on their next update.";
                }
                else if (was.Type != def.Type)
                {
                    yield return $"{name}.{field} changed from {was.Type} to {def.Type}: existing values aren't converted.";
                }
            }

            foreach (var field in before.Fields.Keys.Except(entity.Fields.Keys)) yield return $"{name}.{field} was removed: existing values stay in the records until they're next updated.";
        }

        foreach (var name in old.Entities.Keys.Except(now.Entities.Keys)) yield return $"Entity {name} was removed: its records are kept but no longer reachable by agents.";
    }

    private async Task<JsonObject> UpgradeWorkspaceAsync(string tenantId, string by, string installId, SolutionManifest manifest, SolutionManifest old,
        SolutionWorkspace w, string workspaceId, IReadOnlyDictionary<string, string> inputs, Dictionary<string, Dictionary<string, string>>? secrets, CancellationToken ct)
    {
        var workspace = grains.GetGrain<IWorkspaceGrain>(workspaceId);
        var changes = new JsonArray();
        var problems = new JsonArray();
        var before = old.Workspaces.FirstOrDefault(x => x.Key == w.Key);

        // Only what the package changed is applied, so edits people made in the workspace since
        // (a reworded stage, a tightened policy) survive an upgrade that didn't touch them.
        // The pipeline gets a new version; runs in progress finish on the one they started with.
        if (w.Pipeline is { } pipeline && (before?.Pipeline is not { } was || PipelineDiffers(pipeline, was)))
        {
            var current = await workspace.GetPipeline();
            {
                var changed = await workspace.SetPipeline(pipeline with { Layout = [] }, current?.Version ?? 0, by, $"Upgraded to {manifest.Name} {manifest.Version}");
                if (changed.Success) changes.Add($"pipeline → version {changed.Pipeline?.Version}");
                else problems.Add($"pipeline: {string.Join(" ", changed.Errors)}");
            }
        }

        if (w.Safety is { } safety && JsonSerializer.Serialize(safety) != JsonSerializer.Serialize(before?.Safety))
        {
            await workspace.UpdateSafetyPolicy(safety, by);
            changes.Add("safety policy");
        }

        if (w.DailyTokens != before?.DailyTokens || w.DailyCostUsd != before?.DailyCostUsd)
        {
            await workspace.UpdateBudget(w.DailyTokens, w.DailyCostUsd);
            changes.Add("daily budget");
        }

        // Variables: the package's own (its inputs and declared ones) are replaced; ones people
        // added in the workspace stay.
        var packageOwned = (before?.Variables.Keys.AsEnumerable() ?? []).Concat(old.Inputs.Select(i => i.Key)).ToHashSet();
        var variables = ((await workspace.GetSnapshot())?.Variables ?? []).Where(v => !packageOwned.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);
        foreach (var (k, v) in Variables(w, inputs)) variables[k] = v;
        var variableProblems = await workspace.SetVariables(variables, by);
        foreach (var problem in variableProblems) problems.Add($"variables: {problem}");

        // Triggers, matched by name. A webhook is updated in place (replacing it would change its
        // URL and the secret in it); other changed triggers are replaced.
        var existing = (await workspace.ListTriggers()).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var oldSpecs = (before?.Triggers ?? []).ToDictionary(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var spec in w.Triggers)
        {
            var name = spec.Name.Trim();
            if (existing.TryGetValue(name, out var view))
            {
                var same = oldSpecs.TryGetValue(name, out var oldSpec) && JsonSerializer.Serialize(oldSpec) == JsonSerializer.Serialize(spec) && view.Kind == spec.Kind;
                if (same) continue;
                if (view.Kind == TriggerKind.Webhook && spec.Kind == TriggerKind.Webhook)
                {
                    // Updated in place, so the service sending to it keeps working.
                    var updated = await workspace.UpdateTriggerInstruction(view.TriggerId, spec.Instruction, by);
                    if (updated.Success) changes.Add($"webhook '{name}' updated");
                    else problems.Add($"webhook '{name}': {updated.Message}");
                    continue;
                }

                await workspace.RemoveTrigger(view.TriggerId, by);
                changes.Add($"trigger '{name}' replaced");
            }
            else
            {
                changes.Add($"trigger '{name}' added");
            }

            var added = await workspace.AddTrigger(spec, by, $"solution:{installId}:{w.Key}:{name}:{manifest.Version}:{Guid.NewGuid():n}", revealSecret: true);
            if (!added.Success) problems.Add($"trigger '{name}': {added.Message}");
        }

        foreach (var (name, view) in existing.Where(e => w.Triggers.All(t => !string.Equals(t.Name.Trim(), e.Key, StringComparison.OrdinalIgnoreCase))))
        {
            // Only the solution's own triggers: ones people added in the workspace stay.
            if (!oldSpecs.ContainsKey(name)) continue;
            await workspace.RemoveTrigger(view.TriggerId, by);
            changes.Add($"trigger '{name}' removed");
        }

        // Connections: the records connection follows the data access; connectors are added or removed.
        var connections = (await workspace.ListConnections()).ToDictionary(c => c.Name);
        var dataAccessChanged = JsonSerializer.Serialize(w.Data.OrderBy(k => k.Key)) != JsonSerializer.Serialize((before?.Data ?? []).OrderBy(k => k.Key));
        if (connections.TryGetValue(SolutionValidator.DataConnectionName, out var data))
        {
            if (w.Data.Count == 0)
            {
                await workspace.RemoveConnection(data.ConnectionId);
                changes.Add("records connection removed");
            }
            else if (dataAccessChanged)
            {
                await workspace.RemoveConnection(data.ConnectionId);
                var re = await workspace.AddConnection(DataConnection(tenantId, installId, w.Data));
                if (re.Success) changes.Add("records access changed");
                else problems.Add($"records: {re.Message}");
            }
            else
            {
                // Entities' fields may have changed: list the tools again.
                await workspace.RefreshConnectionTools(data.ConnectionId);
            }
        }
        else if (w.Data.Count > 0)
        {
            var added = await workspace.AddConnection(DataConnection(tenantId, installId, w.Data));
            if (added.Success) changes.Add("records connection added");
            else problems.Add($"records: {added.Message}");
        }

        // A connector moved to (or off, or between) global connectors: link the new one in its place.
        foreach (var name in w.Connectors.Where(connections.ContainsKey))
        {
            if (manifest.Connectors.FirstOrDefault(c => c.Name == name) is not { } c || SameGlobal(c.Global, connections[name].GlobalConnectorName)) continue;
            if (c.Global is null && secrets?.GetValueOrDefault(name) is not { Count: > 0 }) continue;
            await workspace.RemoveConnection(connections[name].ConnectionId);
            var relinked = await workspace.AddConnection(ConnectorConnection(c, secrets));
            if (relinked.Success) changes.Add(c.Global is null ? $"connector '{name}' now uses its own secrets" : $"connector '{name}' now uses the global connector '{c.Global}'");
            else problems.Add($"connector '{name}': {relinked.Message}");
        }

        foreach (var name in w.Connectors.Where(n => !connections.ContainsKey(n)))
        {
            if (manifest.Connectors.FirstOrDefault(c => c.Name == name) is not { } c || plugins.Get(c.Plugin) is null) continue;
            var added = await workspace.AddConnection(ConnectorConnection(c, secrets));
            if (added.Success) changes.Add($"connector '{name}' added");
            else problems.Add($"connector '{name}': {added.Message}");
        }

        foreach (var name in (before?.Connectors ?? []).Where(n => !w.Connectors.Contains(n) && connections.ContainsKey(n)))
        {
            await workspace.RemoveConnection(connections[name].ConnectionId);
            changes.Add($"connector '{name}' removed");
        }

        if (before is not null && before.Name != w.Name) problems.Add($"renamed to '{w.Name}' in the package; rename the workspace by hand if you want the new name.");
        return new JsonObject { ["key"] = w.Key, ["workspace_id"] = workspaceId, ["action"] = changes.Count > 0 ? "updated" : "unchanged", ["changes"] = changes, ["problems"] = problems };
    }

    // ---------------------------------------------------------------- uninstall, read

    public async Task UninstallAsync(string tenantId, string installId, bool purge, string by, CancellationToken ct)
    {
        var row = await RowAsync(tenantId, installId, ct);
        foreach (var id in (JsonSerializer.Deserialize<Dictionary<string, string>>(row.WorkspacesJson) ?? []).Values)
        {
            await grains.GetGrain<IWorkspaceGrain>(id).Archive();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var saved = await db.SolutionInstalls.FirstAsync(s => s.InstallId == installId, ct);
        var history = JsonNode.Parse(saved.HistoryJson) as JsonArray ?? [];
        history.Add(new JsonObject { ["version"] = saved.Version, ["at"] = clock.GetUtcNow(), ["by"] = by, ["action"] = purge ? "uninstall+purge" : "uninstall" });
        saved.HistoryJson = history.ToJsonString();
        saved.Status = nameof(SolutionInstallStatus.Uninstalled);
        saved.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        if (purge)
        {
            var purged = EfSolutionCatalog.ToSolution(row).Manifest;
            foreach (var c in purged.Connectors) await secretStore.DeleteScopeAsync(AssistantSecretScope(installId, c.Name), ct);
            foreach (var (entity, field, _) in IndexedFields(purged))
            {
                await db.Database.ExecuteSqlRawAsync($"DROP INDEX CONCURRENTLY IF EXISTS \"{RecordQuerySql.IndexName(installId, entity, field)}\"", ct);
            }

            await db.SolutionRecords.Where(r => r.TenantId == tenantId && r.InstallId == installId).ExecuteDeleteAsync(ct);
            await db.SolutionRecordChanges.Where(r => r.TenantId == tenantId && r.InstallId == installId).ExecuteDeleteAsync(ct);
            await db.SolutionConversations.Where(r => r.TenantId == tenantId && r.InstallId == installId).ExecuteDeleteAsync(ct);
        }

        catalog.Invalidate(installId);
    }

    private async Task<SolutionInstallRecord> RowAsync(string tenantId, string installId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SolutionInstalls.AsNoTracking().FirstOrDefaultAsync(s => s.InstallId == installId && s.TenantId == tenantId, ct)
               ?? throw new SolutionException($"No solution '{installId}'.", StatusCodes.Status404NotFound);
    }

    /// <summary>The installs, or one by id or by name.</summary>
    public async Task<IReadOnlyList<SolutionInstallRecord>> ListAsync(string tenantId, bool includeUninstalled, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.SolutionInstalls.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (!includeUninstalled) q = q.Where(s => s.Status != nameof(SolutionInstallStatus.Uninstalled));
        return await q.OrderByDescending(s => s.UpdatedAt).ToListAsync(ct);
    }

    public async Task<SolutionInstallRecord> FindAsync(string tenantId, string idOrName, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SolutionInstalls.AsNoTracking()
                   .Where(s => s.TenantId == tenantId && (s.InstallId == idOrName || s.Name == idOrName && s.Status != nameof(SolutionInstallStatus.Uninstalled)))
                   .OrderByDescending(s => s.UpdatedAt).FirstOrDefaultAsync(ct)
               ?? throw new SolutionException($"No solution '{idOrName}'.", StatusCodes.Status404NotFound);
    }

    public async Task<JsonObject> ViewAsync(SolutionInstallRecord row, bool admin, CancellationToken ct)
    {
        var solution = EfSolutionCatalog.ToSolution(row);
        var m = solution.Manifest;
        var counts = new JsonObject();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var grouped = await db.SolutionRecords.AsNoTracking().Where(r => r.TenantId == row.TenantId && r.InstallId == row.InstallId)
                .GroupBy(r => r.Entity).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            foreach (var g in grouped) counts[g.Key] = g.Count;
        }

        return new JsonObject
        {
            ["install_id"] = row.InstallId,
            ["name"] = row.Name,
            ["title"] = row.Title,
            ["version"] = row.Version,
            ["status"] = row.Status,
            ["description"] = m.Description,
            ["installed_by"] = row.InstalledBy,
            ["installed_at"] = row.InstalledAt,
            ["updated_at"] = row.UpdatedAt,
            ["inputs"] = JsonNode.Parse(row.InputsJson),
            ["workspaces"] = new JsonArray([.. solution.Workspaces.Select(w => (JsonNode)new JsonObject
            {
                ["key"] = w.Key,
                ["workspace_id"] = w.Value,
                ["name"] = m.Workspaces.FirstOrDefault(x => x.Key == w.Key)?.Name
            })]),
            ["entities"] = new JsonArray([.. m.Entities.Select(e => (JsonNode)new JsonObject
            {
                ["name"] = e.Key,
                ["description"] = e.Value.Description,
                ["records"] = counts[e.Key]?.GetValue<int>() ?? 0,
                ["title_field"] = RecordSchema.TitleField(e.Value),
                ["fields"] = JsonSerializer.SerializeToNode(e.Value.Fields, SolutionManifestReader.Json)
            })]),
            ["connectors"] = JsonSerializer.SerializeToNode(m.Connectors.Select(c => new { c.Name, c.Plugin, c.Required, c.Description, c.Global }), SolutionManifestReader.Json),
            ["assistant"] = m.Assistant is { } a ? new JsonObject
            {
                ["name"] = a.Name,
                ["greeting"] = a.Greeting,
                ["data"] = JsonSerializer.SerializeToNode(a.Data),
                ["allowed_origins"] = JsonSerializer.SerializeToNode(a.AllowedOrigins),
                ["monthly_cost_limit_usd_per_user"] = a.MonthlyCostLimitUsdPerUser,
                ["max_messages_per_minute"] = a.MaxMessagesPerMinute
            } : null,
            ["own_sign_in"] = m.EndUsers?.Jwt is not null,
            ["evals"] = new JsonArray([.. m.Evals.Select(e => (JsonNode)new JsonObject { ["name"] = e.Name, ["workspace"] = e.Workspace })]),
            ["webhooks"] = new JsonArray([.. m.Webhooks.Select(h => (JsonNode)new JsonObject { ["url"] = h.Url, ["events"] = JsonSerializer.SerializeToNode(h.Events) })]),
            ["webhook_secret"] = admin && m.Webhooks.Count > 0 ? RecordChangeDispatcher.WebhookSecret(protector, row.InstallId) : null,
            ["history"] = JsonNode.Parse(row.HistoryJson)
        };
    }

    // ---------------------------------------------------------------- evals

    /// <summary>
    /// Runs the solution's eval cases for real (docs/solutions.md#evals): each starts a run of its
    /// workspace with its input, waits for it to finish, and checks the run's status, its summary
    /// and the records its agents created or changed. Cases run one after another.
    /// </summary>
    public async Task<JsonObject> RunEvalsAsync(string tenantId, string installId, IReadOnlyCollection<string>? only, string by, CancellationToken ct)
    {
        var row = await RowAsync(tenantId, installId, ct);
        var solution = EfSolutionCatalog.ToSolution(row);
        var cases = solution.Manifest.Evals.Where(e => only is not { Count: > 0 } || only.Contains(e.Name)).ToList();
        if (cases.Count == 0) throw new SolutionException(only is { Count: > 0 } ? "None of those evals exist." : "This solution has no evals.", StatusCodes.Status404NotFound);

        var results = new JsonArray();
        var passed = 0;
        foreach (var e in cases)
        {
            var result = await RunEvalAsync(tenantId, solution, e, by, ct);
            if (result["passed"]!.GetValue<bool>()) passed++;
            results.Add(result);
        }

        return new JsonObject
        {
            ["install_id"] = installId,
            ["version"] = row.Version,
            ["passed"] = passed,
            ["failed"] = cases.Count - passed,
            ["results"] = results
        };
    }

    private async Task<JsonObject> RunEvalAsync(string tenantId, InstalledSolution solution, SolutionEval e, string by, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var failures = new List<string>();
        if (!solution.Workspaces.TryGetValue(e.Workspace, out var workspaceId))
        {
            return new JsonObject { ["name"] = e.Name, ["passed"] = false, ["failures"] = new JsonArray($"Workspace '{e.Workspace}' isn't installed.") };
        }

        var workspace = grains.GetGrain<IWorkspaceGrain>(workspaceId);
        var started = await workspace.StartRunWithData(e.Input, e.InputData?.ToJsonString(), $"eval:{by}");
        if (!started.Success || started.RunId is null)
        {
            return new JsonObject { ["name"] = e.Name, ["passed"] = false, ["failures"] = new JsonArray($"The run didn't start: {started.Message}") };
        }

        WorkspaceRunSummary? run = null;
        var deadline = clock.GetUtcNow().AddSeconds(e.TimeoutSeconds);
        while (clock.GetUtcNow() < deadline && !ct.IsCancellationRequested)
        {
            run = (await workspace.GetSnapshot())?.Runs.FirstOrDefault(r => r.RunId == started.RunId);
            if (run?.CompletedAt is not null) break;
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }

        if (run?.CompletedAt is null) failures.Add($"The run didn't finish within {e.TimeoutSeconds}s (it's {run?.Status.ToString() ?? "unknown"}).");
        else
        {
            if (e.Expect.Status is { Length: > 0 } status && !string.Equals(status, run.Status.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Expected status {status}, got {run.Status}.");
            }

            var summary = run.Summary ?? string.Empty;
            foreach (var phrase in e.Expect.SummaryContains.Where(p => !summary.Contains(p, StringComparison.OrdinalIgnoreCase))) failures.Add($"The summary doesn't mention \"{phrase}\".");
            foreach (var phrase in e.Expect.SummaryNotContains.Where(p => summary.Contains(p, StringComparison.OrdinalIgnoreCase))) failures.Add($"The summary mentions \"{phrase}\".");
        }

        foreach (var (entity, expect) in e.Expect.Records)
        {
            var changed = await records.ChangedByTasksAsync(tenantId, solution.InstallId, entity, [started.RunId], ct);
            var count = changed.Count(r => RecordSchema.Matches(r, expect.Where));
            var where = expect.Where.Count == 0 ? string.Empty : $" with {string.Join(", ", expect.Where.Select(w => $"{w.Key}={w.Value}"))}";
            if (expect.AtLeast is { } min && count < min) failures.Add($"Expected at least {min} {entity} record(s){where} written by the run, found {count}.");
            if (expect.AtMost is { } max && count > max) failures.Add($"Expected at most {max} {entity} record(s){where} written by the run, found {count}.");
        }

        return new JsonObject
        {
            ["name"] = e.Name,
            ["passed"] = failures.Count == 0,
            ["failures"] = new JsonArray([.. failures.Select(f => (JsonNode)JsonValue.Create(f)!)]),
            ["run_id"] = started.RunId,
            ["workspace_id"] = workspaceId,
            ["status"] = run?.Status.ToString(),
            ["summary"] = run?.Summary is { } s ? Clip(s, 1000) : null,
            ["duration_ms"] = watch.ElapsedMilliseconds
        };
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
