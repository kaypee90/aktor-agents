using AgentRuntime.Safety;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// The durable audit log. Each scope (workspace) is one hash chain. Appends lock the scope's head
/// row, so concurrent writers are sequenced without gaps; the unique Key makes a replayed action
/// (after a crash) a no-op. The agent sandbox role (database_query) has no access to these tables.
/// </summary>
public sealed class PostgresAuditLog(IDbContextFactory<AgentDbContext> dbFactory) : IAuditLog
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "AuditHeads" ("Scope", "Seq", "Hash") VALUES ({entry.Scope}, 0, {AuditHasher.Genesis}) ON CONFLICT DO NOTHING""",
            cancellationToken);
        var head = await db.AuditHeads
            .FromSqlInterpolated($"""SELECT * FROM "AuditHeads" WHERE "Scope" = {entry.Scope} FOR UPDATE""")
            .SingleAsync(cancellationToken);

        if (await db.AuditEntries.AnyAsync(a => a.Key == entry.Key, cancellationToken)) return;

        var sequenced = entry with { At = AuditHasher.Normalize(entry.At), Seq = head.Seq + 1, PreviousHash = head.Hash };
        var hash = AuditHasher.Compute(head.Hash, sequenced);
        db.AuditEntries.Add(new AuditRecord
        {
            Scope = sequenced.Scope,
            Seq = sequenced.Seq,
            Key = sequenced.Key,
            At = sequenced.At,
            ActorType = sequenced.ActorType,
            ActorId = sequenced.ActorId,
            ActorName = sequenced.ActorName,
            Action = sequenced.Action,
            Target = sequenced.Target,
            SideEffects = sequenced.SideEffects,
            Outcome = sequenced.Outcome,
            Summary = sequenced.Summary,
            DetailJson = sequenced.DetailJson,
            PreviousHash = sequenced.PreviousHash,
            Hash = hash
        });
        head.Seq = sequenced.Seq;
        head.Hash = hash;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery q, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = db.AuditEntries.AsNoTracking().Where(a => a.Scope == q.Scope);
        if (q.ActorId is { } actor) rows = rows.Where(a => a.ActorId == actor);
        if (q.ActionPrefix is { } prefix) rows = rows.Where(a => a.Action.StartsWith(prefix));
        if (q.Text is { } text) rows = rows.Where(a => EF.Functions.ILike(a.Summary, $"%{text}%") || EF.Functions.ILike(a.Target, $"%{text}%"));
        if (q.Since is { } since) rows = rows.Where(a => a.At >= since);
        if (q.BeforeSeq is { } before) rows = rows.Where(a => a.Seq < before);
        var list = await rows.OrderByDescending(a => a.Seq).Take(q.Limit).ToListAsync(cancellationToken);
        return list.Select(ToEntry).ToList();
    }

    public async Task<AuditVerification> VerifyAsync(string scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AuditEntries.AsNoTracking().Where(a => a.Scope == scope).OrderBy(a => a.Seq).ToListAsync(cancellationToken);
        var result = AuditHasher.Verify(rows.Select(ToEntry).ToList());
        if (!result.Valid) return result;

        // Deleting the newest records leaves a valid prefix; the head row still remembers the end.
        var head = await db.AuditHeads.AsNoTracking().FirstOrDefaultAsync(h => h.Scope == scope, cancellationToken);
        if (head is not null && (head.Seq != rows.Count || (rows.Count > 0 && head.Hash != rows[^1].Hash)))
        {
            return new AuditVerification(false, rows.Count, rows.Count + 1, $"The log ends at record {rows.Count} but should end at {head.Seq}: records were removed.");
        }

        return result;
    }

    private static AuditEntry ToEntry(AuditRecord a) => new()
    {
        Scope = a.Scope,
        Seq = a.Seq,
        Key = a.Key,
        At = a.At,
        ActorType = a.ActorType,
        ActorId = a.ActorId,
        ActorName = a.ActorName,
        Action = a.Action,
        Target = a.Target,
        SideEffects = a.SideEffects,
        Outcome = a.Outcome,
        Summary = a.Summary,
        DetailJson = a.DetailJson,
        PreviousHash = a.PreviousHash,
        Hash = a.Hash
    };
}
