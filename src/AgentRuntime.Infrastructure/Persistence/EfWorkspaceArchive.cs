using System.Text.Json;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>Upserts one row per workspace with its latest snapshot.</summary>
public sealed class EfWorkspaceArchive(IDbContextFactory<AgentDbContext> dbFactory) : IWorkspaceArchive
{
    public async Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Workspaces.FindAsync([snapshot.WorkspaceId], cancellationToken);
        if (record is null)
        {
            record = new WorkspaceRecord { WorkspaceId = snapshot.WorkspaceId, Name = snapshot.Name, CreatedAt = snapshot.CreatedAt };
            db.Workspaces.Add(record);
        }

        record.Name = snapshot.Name;
        record.Goal = snapshot.Goal;
        record.Status = snapshot.Status.ToString();
        record.Agents = snapshot.Agents.Count;
        record.Triggers = snapshot.Triggers.Count;
        record.TotalTokens = snapshot.TotalTokens;
        record.TotalCostUsd = snapshot.TotalCostUsd;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        record.SnapshotJson = JsonSerializer.Serialize(snapshot, EfWorldArchive.SnapshotJsonOptions);

        await db.SaveChangesAsync(cancellationToken);
    }
}
