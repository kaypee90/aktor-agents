using System.Text.Json;
using AgentRuntime.Skills;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>Skills in Postgres (table "Skills"), keyed by organization, workspace ("" for the whole
/// organization) and name.</summary>
public sealed class PostgresSkillStore(IDbContextFactory<AgentDbContext> dbFactory) : ISkillStore
{
    private static string Ws(string? workspaceId) => workspaceId ?? string.Empty;

    public async Task<IReadOnlyList<SkillSummary>> ListAsync(string tenantId, bool enabledOnly, string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var ws = Ws(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Skills.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.WorkspaceId == ws && (!enabledOnly || s.Enabled))
            .OrderBy(s => s.Name)
            .Select(s => new { s.Name, s.Description, s.Version, s.Enabled, s.UpdatedAt, s.FilesJson })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new SkillSummary(r.Name, r.Description, r.Version, r.Enabled, r.UpdatedAt, Files(r.FilesJson).Count,
            ws.Length == 0 ? null : ws)).ToList();
    }

    public async Task<SkillDocument?> GetAsync(string tenantId, string name, string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var ws = Ws(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var r = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.WorkspaceId == ws && s.Name == name, cancellationToken);
        return r is null ? null : new SkillDocument
        {
            Name = r.Name,
            Description = r.Description,
            Instructions = r.Instructions,
            Files = Files(r.FilesJson),
            Version = r.Version,
            Enabled = r.Enabled,
            UpdatedAt = r.UpdatedAt,
            UpdatedBy = r.UpdatedBy,
            WorkspaceId = ws.Length == 0 ? null : ws
        };
    }

    public async Task<SkillDocument> SaveAsync(string tenantId, SkillDocument skill, string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var ws = Ws(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var row = await db.Skills.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.WorkspaceId == ws && s.Name == skill.Name, cancellationToken);
        if (row is null)
        {
            row = new SkillRecord
            {
                TenantId = tenantId,
                WorkspaceId = ws,
                Name = skill.Name,
                Description = skill.Description,
                Instructions = skill.Instructions,
                CreatedAt = now
            };
            db.Skills.Add(row);
        }
        else
        {
            row.Version++;
        }

        row.Description = skill.Description;
        row.Instructions = skill.Instructions;
        row.FilesJson = JsonSerializer.Serialize(skill.Files);
        row.UpdatedAt = now;
        row.UpdatedBy = skill.UpdatedBy;
        await db.SaveChangesAsync(cancellationToken);
        return skill with { Version = row.Version, Enabled = row.Enabled, UpdatedAt = now, WorkspaceId = ws.Length == 0 ? null : ws };
    }

    public async Task<bool> SetEnabledAsync(string tenantId, string name, bool enabled, string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var ws = Ws(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Skills.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.WorkspaceId == ws && s.Name == name, cancellationToken);
        if (row is null) return false;
        row.Enabled = enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(string tenantId, string name, string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var ws = Ws(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Skills.Where(s => s.TenantId == tenantId && s.WorkspaceId == ws && s.Name == name).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    private static List<SkillFile> Files(string json) => JsonSerializer.Deserialize<List<SkillFile>>(json) ?? [];
}
