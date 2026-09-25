using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Simulation;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>Upserts one row per world with its latest snapshot (written at every tick).</summary>
public sealed class EfWorldArchive(IDbContextFactory<AgentDbContext> dbFactory) : IWorldArchive
{
    /// <summary>Same shape the API returns for a live world, so archived snapshots can be served as-is.</summary>
    public static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(WorldSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Worlds.FindAsync([snapshot.WorldId], cancellationToken);
        if (record is null)
        {
            record = new WorldRecord { WorldId = snapshot.WorldId, Name = snapshot.Name, CreatedAt = snapshot.CreatedAt };
            db.Worlds.Add(record);
        }

        record.TenantId = AgentRuntime.Tenancy.TenantIds.Normalize(snapshot.TenantId);
        record.Name = snapshot.Name;
        record.Seed = snapshot.Seed;
        record.Status = snapshot.Status.ToString();
        record.Tick = snapshot.Tick;
        record.MaxTicks = snapshot.MaxTicks;
        record.Residents = snapshot.Totals.TotalResidents;
        record.CostUsd = snapshot.Totals.CostUsd;
        record.EndedAt = snapshot.EndedAt;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        record.SnapshotJson = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);

        await db.SaveChangesAsync(cancellationToken);
    }
}
