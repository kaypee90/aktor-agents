using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Integrations;
using AgentRuntime.Memory;
using AgentRuntime.Skills;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

public sealed class WorkspaceCopyException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Cloning a workspace, exporting it as an organization template (and creating workspaces from
/// those), and deleting archived workspaces (docs/workspaces.md, docs/templates.md). A copy gets the
/// workspace's definition (goal, pipeline, safety policy, budget, triggers, connections) and,
/// when asked, its own skills and knowledge; never its chat, runs, files or history.
/// </summary>
public sealed class WorkspaceCopyService(
    IGrainFactory grains,
    IDbContextFactory<AgentDbContext> dbFactory,
    IMemoryStore memory,
    ISkillStore skills,
    IOptions<ToolsOptions> toolsOptions,
    ILogger<WorkspaceCopyService> logger)
{
    public const string TemplatePrefix = "org-";

    /// <summary>Definitions as stored and downloaded: web-style names, enums as names.</summary>
    public static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private async Task CheckPlanAsync(string tenantId, CancellationToken ct)
    {
        var plan = await grains.GetGrain<Tenancy.ITenantGrain>(tenantId).GetPlan();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (plan.MaxWorkspaces > 0 &&
            await db.Workspaces.CountAsync(w => w.TenantId == tenantId && w.Status != "Archived" && w.Kind != "study", ct) >= plan.MaxWorkspaces)
        {
            throw new WorkspaceCopyException($"The {plan.Name} plan allows {plan.MaxWorkspaces} active workspaces. Archive one or upgrade.", 402);
        }
    }

    private async Task<IWorkspaceGrain> OwnedAsync(string tenantId, string workspaceId)
    {
        var workspace = grains.GetGrain<IWorkspaceGrain>(workspaceId);
        if (!WorkspaceIds.IsWorkspace(workspaceId) || !Tenancy.TenantIds.Same(await workspace.GetTenantId() ?? "\0", tenantId))
        {
            throw new WorkspaceCopyException($"No workspace '{workspaceId}'.", StatusCodes.Status404NotFound);
        }

        return workspace;
    }

    /// <summary>A new workspace from a definition: created, then its connections and triggers added
    /// one by one, each reported (a connection whose secrets weren't copied fails with the reason).
    /// Adding connections needs the Admin role, as it does by hand: for anyone else they're listed,
    /// not added.</summary>
    public async Task<(string WorkspaceId, List<object> Connections, List<object> Triggers)> CreateFromDefinitionAsync(
        string tenantId, string ownerId, WorkspaceDefinition definition, string name, string? templateId, bool addConnections, CancellationToken ct)
    {
        await CheckPlanAsync(tenantId, ct);
        var id = WorkspaceIds.New();
        var workspace = grains.GetGrain<IWorkspaceGrain>(id);
        await workspace.Create(new WorkspaceCreationRequest
        {
            Name = name,
            Goal = definition.Goal,
            DailyTokenLimit = definition.DailyTokenLimit > 0 ? definition.DailyTokenLimit : null,
            DailyCostLimitUsd = definition.DailyCostLimitUsd > 0 ? definition.DailyCostLimitUsd : null,
            TenantId = tenantId,
            OwnerId = ownerId,
            TemplateId = templateId ?? definition.TemplateId,
            SafetyPolicy = definition.SafetyPolicy,
            Pipeline = definition.Pipeline,
            CreatedBy = ownerId
        });

        var connections = new List<object>();
        foreach (var request in definition.Connections)
        {
            if (!addConnections)
            {
                connections.Add(new { name = request.Name, plugin_id = request.PluginId, ok = false, message = "Not added: an Admin adds connections." });
                continue;
            }

            var added = await workspace.AddConnection(request);
            if (added.Success && definition.DisabledTools.TryGetValue(request.Name, out var disabled) && disabled.Count > 0
                && (await workspace.ListConnections()).FirstOrDefault(c => c.Name == request.Name) is { } view)
            {
                // Switched-off tools are kept by their own names; the view shows them as {connection}__{tool}.
                var off = disabled.Select(local => ConnectionNames.Expose(request.Name, local)).ToHashSet();
                await workspace.UpdateConnection(view.ConnectionId, new ConnectionUpdate
                {
                    EnabledTools = view.Tools.Where(t => !off.Contains(t.Name)).Select(t => t.Name).ToList()
                });
            }

            connections.Add(new { name = request.Name, plugin_id = request.PluginId, ok = added.Success, message = added.Message });
        }

        var triggers = new List<object>();
        for (var i = 0; i < definition.Triggers.Count; i++)
        {
            var spec = definition.Triggers[i];
            var added = await workspace.AddTrigger(spec, ownerId, $"copy:{id}:{i}", revealSecret: true);
            string? path = null;
            if (added.ResultJson is { } json)
            {
                using var doc = JsonDocument.Parse(json);
                path = doc.RootElement.TryGetProperty("webhook_path", out var p) ? p.GetString() : null;
            }

            triggers.Add(new { name = spec.Name, kind = spec.Kind.ToString(), ok = added.Success, message = added.Message, webhook_path = path });
        }

        return (id, connections, triggers);
    }

    /// <summary>
    /// A copy of the workspace's setup: goal, pipeline, safety policy, budget, triggers, and its own
    /// skills and knowledge; for an Admin also its connections with their secrets (copied inside the
    /// server, never returned). Never its runs, files, chat or history.
    /// </summary>
    public async Task<object> CloneAsync(string tenantId, string sourceId, string? name, bool admin, bool copyKnowhow, string by, CancellationToken ct)
    {
        var source = await OwnedAsync(tenantId, sourceId);
        var definition = await source.ExportDefinition(includeSecrets: admin)
                         ?? throw new WorkspaceCopyException($"No workspace '{sourceId}'.", StatusCodes.Status404NotFound);
        var copyName = string.IsNullOrWhiteSpace(name) ? $"{definition.Name} (copy)" : name.Trim();
        var (id, connections, triggers) = await CreateFromDefinitionAsync(tenantId, by, definition, copyName.Length > 100 ? copyName[..100] : copyName, null, admin, ct);

        int skillsCopied = 0, knowledgeCopied = 0;
        if (copyKnowhow)
        {
            foreach (var summary in await skills.ListAsync(tenantId, enabledOnly: false, sourceId, ct))
            {
                if (await skills.GetAsync(tenantId, summary.Name, sourceId, ct) is not { } skill) continue;
                await skills.SaveAsync(tenantId, skill with { WorkspaceId = id, UpdatedBy = by, UpdatedAt = DateTimeOffset.UtcNow }, id, ct);
                if (!skill.Enabled) await skills.SetEnabledAsync(tenantId, skill.Name, false, id, ct);
                skillsCopied++;
            }

            var scope = MemoryScope.OnlyWorkspace(sourceId);
            foreach (var (key, agentId) in await memory.ListSharedKeysAsync(tenantId, scope, ct))
            {
                if (await memory.ReadAsync(tenantId, agentId, key, scope, ct) is not { } entry || entry.WorkspaceId != sourceId) continue;
                await memory.WriteAsync(entry with { MemoryId = Guid.NewGuid().ToString("n"), WorkspaceId = id, CreatedAt = DateTimeOffset.UtcNow, Score = null }, ct);
                knowledgeCopied++;
            }
        }

        return new
        {
            workspace_id = id,
            name = copyName,
            connections,
            triggers,
            connections_without_secrets = definition.ConnectionsWithoutSecrets,
            skills = skillsCopied,
            knowledge = knowledgeCopied
        };
    }

    // ---------------------------------------------------------------- templates

    public async Task<OrganizationTemplateRecord> ExportTemplateAsync(string tenantId, string sourceId, string? name, string? description,
        string? category, string by, CancellationToken ct)
    {
        var source = await OwnedAsync(tenantId, sourceId);
        var definition = await source.ExportDefinition(includeSecrets: false)
                         ?? throw new WorkspaceCopyException($"No workspace '{sourceId}'.", StatusCodes.Status404NotFound);
        return await SaveTemplateAsync(tenantId, definition with { TemplateId = null }, name ?? definition.Name, description, category, sourceId, by, ct);
    }

    /// <summary>A template from a downloaded template file (another server's, or edited by hand).</summary>
    public async Task<OrganizationTemplateRecord> ImportTemplateAsync(string tenantId, string json, string by, CancellationToken ct)
    {
        TemplateFile? file;
        try
        {
            file = JsonSerializer.Deserialize<TemplateFile>(json, DefinitionJson);
        }
        catch (JsonException ex)
        {
            throw new WorkspaceCopyException($"Not a template file: {ex.Message}");
        }

        if (file?.Definition is not { } definition || string.IsNullOrWhiteSpace(file.Name))
        {
            throw new WorkspaceCopyException("Not a template file: it needs a name and a definition.");
        }

        // Secrets never travel in a template file, whatever it claims.
        definition = definition with
        {
            TemplateId = null,
            Connections = definition.Connections.Select(c => c with { Secrets = [] }).ToList(),
            ConnectionsWithoutSecrets = []
        };
        if (definition.Pipeline is { } pipeline && Pipelines.PipelineValidator.Validate(pipeline, new Pipelines.PipelineOptions()).FirstOrDefault() is { } problem)
        {
            throw new WorkspaceCopyException($"The template's pipeline isn't valid: {problem}");
        }

        return await SaveTemplateAsync(tenantId, definition, file.Name, file.Description, file.Category, null, by, ct);
    }

    private async Task<OrganizationTemplateRecord> SaveTemplateAsync(string tenantId, WorkspaceDefinition definition, string name, string? description,
        string? category, string? sourceId, string by, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new WorkspaceCopyException("A template needs a name of up to 100 characters.");
        var text = (description ?? definition.Goal).Trim();
        var group = string.IsNullOrWhiteSpace(category) ? "Custom" : category.Trim();
        var record = new OrganizationTemplateRecord
        {
            TemplateId = TemplatePrefix + Guid.NewGuid().ToString("n")[..10],
            TenantId = tenantId,
            Name = name,
            Category = group.Length > 40 ? group[..40] : group,
            Description = text.Length > 600 ? text[..600] : text,
            DefinitionJson = JsonSerializer.Serialize(definition with { Connections = definition.Connections.Select(c => c with { Secrets = [] }).ToList() }, DefinitionJson),
            SourceWorkspaceId = sourceId,
            CreatedBy = by,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.OrganizationTemplates.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<OrganizationTemplateRecord>> ListTemplatesAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OrganizationTemplates.AsNoTracking().Where(t => t.TenantId == tenantId).OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    public async Task<OrganizationTemplateRecord?> GetTemplateAsync(string tenantId, string templateId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OrganizationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId && t.TemplateId == templateId, ct);
    }

    public async Task<bool> DeleteTemplateAsync(string tenantId, string templateId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OrganizationTemplates.Where(t => t.TenantId == tenantId && t.TemplateId == templateId).ExecuteDeleteAsync(ct) > 0;
    }

    public static WorkspaceDefinition DefinitionOf(OrganizationTemplateRecord record) =>
        JsonSerializer.Deserialize<WorkspaceDefinition>(record.DefinitionJson, DefinitionJson)
        ?? throw new WorkspaceCopyException("The template is damaged.", StatusCodes.Status500InternalServerError);

    /// <summary>The downloadable form of a template.</summary>
    public sealed record TemplateFile(string Name, string? Description, string? Category, WorkspaceDefinition? Definition, int Format = 1);

    public static string ToFile(OrganizationTemplateRecord record) =>
        JsonSerializer.Serialize(new TemplateFile(record.Name, record.Description, record.Category, DefinitionOf(record)), DefinitionJson);

    // ---------------------------------------------------------------- delete

    /// <summary>
    /// Deletes an archived workspace: its state, connections and secrets (in the grain), its own
    /// knowledge, skills and files, and its row. Its runs' history (and their files) and the audit
    /// log are kept: they're the record of what happened.
    /// </summary>
    public async Task DeleteAsync(string tenantId, string workspaceId, string by, CancellationToken ct)
    {
        var workspace = await OwnedAsync(tenantId, workspaceId);
        await using (var check = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await check.Studies.AnyAsync(s => s.WorkspaceId == workspaceId, ct))
            {
                throw new WorkspaceCopyException("This workspace belongs to a study; delete the study instead.", StatusCodes.Status409Conflict);
            }
        }

        var deleted = await workspace.Delete(by);
        if (!deleted.Success) throw new WorkspaceCopyException(deleted.Message, StatusCodes.Status409Conflict);

        var scope = MemoryScope.OnlyWorkspace(workspaceId);
        // Search returns a page at a time: delete until nothing's left (bounded, in case a write races).
        for (var i = 0; i < 100; i++)
        {
            var page = await memory.SearchAsync(tenantId, string.Empty, Contracts.MemoryKind.Shared, scope: scope, cancellationToken: ct);
            if (page.Count == 0) break;
            await memory.DeleteSharedAsync(tenantId, scope, memoryIds: page.Select(m => m.MemoryId).ToList(), cancellationToken: ct);
        }

        foreach (var skill in await skills.ListAsync(tenantId, enabledOnly: false, workspaceId, ct)) await skills.DeleteAsync(tenantId, skill.Name, workspaceId, ct);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.Artifacts.Where(a => a.TenantId == tenantId && a.TaskId == workspaceId).ExecuteDeleteAsync(ct);
            await db.Workspaces.Where(w => w.WorkspaceId == workspaceId && w.TenantId == tenantId).ExecuteDeleteAsync(ct);
        }

        try
        {
            var root = WorkspacePath.TaskRoot(toolsOptions.Value, workspaceId);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't remove the files of workspace {WorkspaceId}", workspaceId);
        }
    }
}
