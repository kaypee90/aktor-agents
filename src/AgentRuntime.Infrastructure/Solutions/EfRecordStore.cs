using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Solutions;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>
/// Solution records in Postgres (JSONB). Scoping is in every query: the organization and install
/// always, and the end user for entities that have an end_user field when the scope names one.
/// Each write adds its change to the outbox in the same SaveChanges (one transaction), so record
/// triggers and webhooks see every change exactly as it was committed.
/// </summary>
public sealed class EfRecordStore(IDbContextFactory<AgentDbContext> dbFactory, TimeProvider clock) : IRecordStore
{
    public const int MaxLimit = RecordQuerySql.MaxLimit;

    private static IQueryable<SolutionRecordEntity> Scoped(AgentDbContext db, RecordScope scope, string entity, EntityDefinition definition)
    {
        var q = db.SolutionRecords.Where(r => r.TenantId == scope.TenantId && r.InstallId == scope.InstallId && r.Entity == entity);
        if (scope.EndUserId is { } user && RecordSchema.EndUserField(definition) is not null) q = q.Where(r => r.EndUserId == user);
        return q;
    }

    private static SolutionRecord ToRecord(SolutionRecordEntity r) => new()
    {
        RecordId = r.RecordId,
        Entity = r.Entity,
        Data = RecordSchema.ParseObject(r.DataJson) ?? [],
        Version = r.Version,
        EndUserId = r.EndUserId,
        CreatedBy = r.CreatedBy,
        UpdatedBy = r.UpdatedBy,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt
    };

    public async Task<(IReadOnlyList<SolutionRecord> Items, int Total)> ListAsync(RecordScope scope, string entity, EntityDefinition definition, RecordQuery query,
        CancellationToken ct = default)
    {
        // Built twice from the same inputs (a parameter belongs to one command): once to count,
        // once for the page.
        var count = RecordQuerySql.Build(scope, entity, definition, query, clock.GetUtcNow(), page: false);
        var page = RecordQuerySql.Build(scope, entity, definition, query, clock.GetUtcNow(), page: true);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var total = await db.SolutionRecords.FromSqlRaw(count.Sql, count.Parameters).CountAsync(ct);
        var rows = await db.SolutionRecords.FromSqlRaw(page.Sql, page.Parameters).AsNoTracking().ToListAsync(ct);
        return (rows.Select(ToRecord).ToList(), total);
    }

    public async Task<SolutionRecord?> GetAsync(RecordScope scope, string entity, EntityDefinition definition, string recordId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await Scoped(db, scope, entity, definition).AsNoTracking().FirstOrDefaultAsync(r => r.RecordId == recordId, ct);
        return row is null ? null : ToRecord(row);
    }

    /// <summary>Every ref field must point at a record of its entity the scope can see.</summary>
    private static async Task<List<string>> CheckRefsAsync(AgentDbContext db, RecordScope scope, SolutionManifest manifest, EntityDefinition definition,
        JsonObject data, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var (field, def) in definition.Fields.Where(f => f.Value.Type == FieldTypes.Ref))
        {
            if (data[field] is not JsonValue v || !v.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id)) continue;
            var target = manifest.Entities[def.Entity!];
            var exists = await Scoped(db, scope, def.Entity!, target).AnyAsync(r => r.RecordId == id, ct);
            if (!exists) errors.Add($"'{field}': no {def.Entity} '{id}'.");
        }

        return errors;
    }

    private SolutionRecordChangeEntity Change(RecordScope scope, string entity, string recordId, string evt, string data, string? previous, RecordWriter writer) => new()
    {
        TenantId = scope.TenantId,
        InstallId = scope.InstallId,
        Entity = entity,
        RecordId = recordId,
        Event = evt,
        DataJson = data,
        PreviousJson = previous,
        ChangedBy = writer.By,
        TaskId = writer.TaskId,
        At = clock.GetUtcNow()
    };

    public async Task<RecordWriteResult> CreateAsync(RecordScope scope, SolutionManifest manifest, string entity, JsonObject data, RecordWriter writer,
        string? recordId = null, CancellationToken ct = default)
    {
        if (!manifest.Entities.TryGetValue(entity, out var definition)) return RecordWriteResult.Fail($"'{entity}' isn't an entity of this solution.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (recordId is not null && await db.SolutionRecords.AsNoTracking().FirstOrDefaultAsync(r => r.RecordId == recordId, ct) is { } existing)
        {
            // A replayed create (same tool call after a crash): the record it made, if it's this scope's.
            return existing.TenantId == scope.TenantId && existing.InstallId == scope.InstallId && existing.Entity == entity &&
                   (scope.EndUserId is null || RecordSchema.EndUserField(definition) is null || existing.EndUserId == scope.EndUserId)
                ? RecordWriteResult.Ok(ToRecord(existing), replayed: true)
                : RecordWriteResult.Fail("That record id is taken.");
        }

        var (full, errors) = RecordSchema.Apply(definition, data, null, scope.EndUserId);
        if (full is null) return RecordWriteResult.Fail([.. errors]);
        var refErrors = await CheckRefsAsync(db, scope, manifest, definition, full, ct);
        if (refErrors.Count > 0) return RecordWriteResult.Fail([.. refErrors]);

        var now = clock.GetUtcNow();
        var endUserField = RecordSchema.EndUserField(definition);
        var row = new SolutionRecordEntity
        {
            RecordId = recordId ?? SolutionIds.NewRecord(),
            TenantId = scope.TenantId,
            InstallId = scope.InstallId,
            Entity = entity,
            DataJson = full.ToJsonString(),
            EndUserId = endUserField is not null && full[endUserField] is JsonValue owner && owner.TryGetValue<string>(out var o) ? o : null,
            CreatedBy = writer.By,
            UpdatedBy = writer.By,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SolutionRecords.Add(row);
        var record = ToRecord(row);
        db.SolutionRecordChanges.Add(Change(scope, entity, row.RecordId, RecordEvents.Created, record.ToJson().ToJsonString(), null, writer));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (recordId is not null)
        {
            // Two attempts of the same call raced: the other one made it.
            await using var again = await dbFactory.CreateDbContextAsync(ct);
            if (await again.SolutionRecords.AsNoTracking().FirstOrDefaultAsync(r => r.RecordId == recordId, ct) is { } made) return RecordWriteResult.Ok(ToRecord(made), replayed: true);
            throw;
        }

        return RecordWriteResult.Ok(record);
    }

    public async Task<RecordWriteResult> UpdateAsync(RecordScope scope, SolutionManifest manifest, string entity, string recordId, JsonObject changes, RecordWriter writer,
        int? expectedVersion = null, CancellationToken ct = default)
    {
        if (!manifest.Entities.TryGetValue(entity, out var definition)) return RecordWriteResult.Fail($"'{entity}' isn't an entity of this solution.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await Scoped(db, scope, entity, definition).FirstOrDefaultAsync(r => r.RecordId == recordId, ct);
        if (row is null) return RecordWriteResult.Missing(entity, recordId);
        if (expectedVersion is { } expected && expected != row.Version)
        {
            return new RecordWriteResult { Conflict = true, Errors = [$"{entity} '{recordId}' changed since version {expected} (it's at {row.Version}); read it again."] };
        }

        var before = ToRecord(row);
        var (full, errors) = RecordSchema.Apply(definition, changes, before.Data, scope.EndUserId);
        if (full is null) return RecordWriteResult.Fail([.. errors]);
        var refErrors = await CheckRefsAsync(db, scope, manifest, definition, full, ct);
        if (refErrors.Count > 0) return RecordWriteResult.Fail([.. refErrors]);

        // Writing the same values again (a replayed update) changes nothing and emits nothing.
        if (JsonNode.DeepEquals(full, before.Data)) return RecordWriteResult.Ok(before, replayed: true);

        row.DataJson = full.ToJsonString();
        row.Version++;
        row.UpdatedBy = writer.By;
        row.UpdatedAt = clock.GetUtcNow();
        var after = ToRecord(row);
        db.SolutionRecordChanges.Add(Change(scope, entity, recordId, RecordEvents.Updated, after.ToJson().ToJsonString(), before.ToJson().ToJsonString(), writer));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new RecordWriteResult { Conflict = true, Errors = [$"{entity} '{recordId}' was changed by someone else at the same time; read it again."] };
        }

        return RecordWriteResult.Ok(after);
    }

    public async Task<RecordWriteResult> DeleteAsync(RecordScope scope, SolutionManifest manifest, string entity, string recordId, RecordWriter writer,
        CancellationToken ct = default)
    {
        if (!manifest.Entities.TryGetValue(entity, out var definition)) return RecordWriteResult.Fail($"'{entity}' isn't an entity of this solution.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await Scoped(db, scope, entity, definition).FirstOrDefaultAsync(r => r.RecordId == recordId, ct);
        if (row is null) return RecordWriteResult.Missing(entity, recordId);

        // Records that point at this one would be left dangling.
        foreach (var (otherName, other) in manifest.Entities)
        {
            foreach (var (field, _) in other.Fields.Where(f => f.Value.Type == FieldTypes.Ref && f.Value.Entity == entity))
            {
                var probe = new JsonObject { [field] = recordId }.ToJsonString();
                if (await db.SolutionRecords.AnyAsync(r => r.TenantId == scope.TenantId && r.InstallId == scope.InstallId && r.Entity == otherName &&
                                                            EF.Functions.JsonContains(r.DataJson, probe), ct))
                {
                    return RecordWriteResult.Fail($"{entity} '{recordId}' is referenced by a {otherName} ({field}); delete or change that first.");
                }
            }
        }

        var record = ToRecord(row);
        db.SolutionRecords.Remove(row);
        db.SolutionRecordChanges.Add(Change(scope, entity, recordId, RecordEvents.Deleted, record.ToJson().ToJsonString(), null, writer));
        await db.SaveChangesAsync(ct);
        return RecordWriteResult.Ok(record);
    }

    public async Task<IReadOnlyList<JsonObject>> ChangedByTasksAsync(string tenantId, string installId, string entity, IReadOnlyCollection<string> taskIds,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changes = await db.SolutionRecordChanges.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.InstallId == installId && c.Entity == entity && c.TaskId != null && taskIds.Contains(c.TaskId))
            .OrderBy(c => c.ChangeId)
            .Select(c => new { c.RecordId, c.Event, c.DataJson })
            .ToListAsync(ct);
        return changes.GroupBy(c => c.RecordId)
            .Select(g => g.Last())
            .Where(c => c.Event != RecordEvents.Deleted)
            .Select(c => RecordSchema.ParseObject(c.DataJson) ?? [])
            .ToList();
    }
}
