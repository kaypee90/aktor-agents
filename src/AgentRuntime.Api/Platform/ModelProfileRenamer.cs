using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.LLM;
using AgentRuntime.Pipelines;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>What a rename touched.</summary>
public sealed record ModelProfileRename(string OldId, string NewId, int Workspaces, int Templates, int Tasks, int Agents);

/// <summary>
/// Keeps a model profile's id in step with its name (docs/llm-settings.md). A profile's id is made
/// from its name when it's created; when the name changes, the id is changed to match and every
/// reference follows: the organization's workspace pipelines (with their saved versions and
/// <c>@mentions</c>), its saved templates, and the recorded tasks and agents. The old id
/// stays as an alias in the settings, so a running agent or anything missed still finds the profile.
/// </summary>
public sealed class ModelProfileRenamer(
    LlmSettingsService settings,
    IGrainFactory grains,
    IDbContextFactory<AgentDbContext> dbFactory,
    ILogger<ModelProfileRenamer> logger)
{
    /// <summary>The id a profile with this name should have.</summary>
    public static string IdFor(ModelProfile profile) => ModelProfiles.Slug(profile.Name);

    /// <summary>Renames the profile to the id its name gives, unless that id is taken; null when nothing changed.</summary>
    public async Task<ModelProfileRename?> SyncAsync(string tenantId, string profileId, CancellationToken ct = default)
    {
        var models = await settings.GetAsync(tenantId, ct);
        if (models.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile) return null;
        var target = IdFor(profile);
        if (target == profileId || models.Profiles.Any(p => p.Id == target)) return null;
        return await RenameAsync(tenantId, profileId, target, ct);
    }

    public async Task<ModelProfileRename?> RenameAsync(string tenantId, string oldId, string newId, CancellationToken ct = default)
    {
        var tenant = Tenancy.TenantIds.Normalize(tenantId);
        if (!await settings.RenameProfileAsync(tenant, oldId, newId, ct)) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var workspaces = 0;
        foreach (var workspaceId in await db.Workspaces.Where(w => w.TenantId == tenant).Select(w => w.WorkspaceId).ToListAsync(ct))
        {
            try
            {
                if (await grains.GetGrain<IWorkspaceGrain>(workspaceId).RenameModelProfile(oldId, newId)) workspaces++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The alias still covers this workspace's stages.
                logger.LogWarning(ex, "Couldn't move workspace {WorkspaceId} to model id {NewId}", workspaceId, newId);
            }
        }

        var templates = 0;
        foreach (var template in await db.OrganizationTemplates.Where(t => t.TenantId == tenant).ToListAsync(ct))
        {
            if (RenameInJson(template.DefinitionJson, oldId, newId) is { } json)
            {
                template.DefinitionJson = json;
                templates++;
            }
        }

        await db.SaveChangesAsync(ct);

        var tasks = await db.Tasks.Where(t => t.TenantId == tenant && t.ModelProfileId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ModelProfileId, newId), ct);
        var agents = await db.Agents.Where(a => a.TenantId == tenant && a.ModelProfileId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.ModelProfileId, newId), ct);

        logger.LogInformation("Model profile {OldId} is now {NewId}: {Workspaces} workspaces, {Templates} templates, {Tasks} tasks, {Agents} agents",
            oldId, newId, workspaces, templates, tasks, agents);
        return new ModelProfileRename(oldId, newId, workspaces, templates, tasks, agents);
    }

    /// <summary>A saved template's JSON with the profile renamed in its stages; null when it doesn't name it.</summary>
    public static string? RenameInJson(string json, string oldId, string newId)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        var changed = false;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (value is JsonValue v && v.TryGetValue<string>(out var text))
                        {
                            var normalized = key.Replace("_", "").ToLowerInvariant();
                            var next = normalized == "modelprofileid" && text == oldId ? newId
                                : normalized == "instructions" ? Mentions.RenameHandle(text, oldId, newId)
                                : text;
                            if (next != text)
                            {
                                obj[key] = next;
                                changed = true;
                            }
                        }
                        else
                        {
                            Walk(value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array) Walk(item);
                    break;
            }
        }

        Walk(root);
        return changed ? root!.ToJsonString() : null;
    }
}
