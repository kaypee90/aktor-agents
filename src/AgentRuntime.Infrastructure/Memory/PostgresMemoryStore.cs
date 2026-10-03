using System.Data.Common;
using System.Globalization;
using System.Text;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AgentRuntime.Infrastructure.Memory;

/// <summary>
/// Postgres-backed <see cref="IMemoryStore"/> (CLAUDE.md section 20, roadmap P4) with hybrid
/// search: pgvector similarity on embeddings from the configured <see cref="IEmbeddingProvider"/>,
/// full-text and substring matching on the words, and a recency boost, combined into one score.
///
/// <para>Degrades rather than fails: with no embedding provider, or a Postgres without the pgvector
/// extension (the migration only adds the column where the extension is available), search runs on
/// keywords and recency alone. Every query is filtered by tenant first, so another organization's
/// entries are never candidates, however similar.</para>
///
/// <para>Registered as a singleton (tools depend on it), it opens a short-lived DbContext per call.</para>
/// </summary>
public sealed class PostgresMemoryStore(
    IDbContextFactory<AgentDbContext> dbContextFactory,
    IEmbeddingProvider embeddings,
    IOptions<MemoryOptions> options,
    ILogger<PostgresMemoryStore> logger) : IMemoryStore
{
    private readonly MemorySearchOptions _search = options.Value.Search;
    private bool? _vectorColumn;

    /// <summary>For callers (and older tests) without embedding support: keyword search only.</summary>
    public PostgresMemoryStore(IDbContextFactory<AgentDbContext> dbContextFactory)
        : this(dbContextFactory, new NullEmbeddingProvider(), Options.Create(new MemoryOptions()), NullLogger<PostgresMemoryStore>.Instance)
    {
    }

    public async Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.MemoryEntries
            .FirstOrDefaultAsync(m => m.TenantId == record.TenantId && m.AgentId == record.AgentId && m.Key == record.Key &&
                                      m.WorkspaceId == record.WorkspaceId, cancellationToken);

        string memoryId;
        if (existing is not null)
        {
            existing.Value = record.Value;
            existing.Kind = record.Kind.ToString();
            existing.EmbeddingModel = null; // the old vector describes the old value
            memoryId = existing.MemoryId;
        }
        else
        {
            db.MemoryEntries.Add(new MemoryEntity
            {
                MemoryId = record.MemoryId,
                TenantId = record.TenantId,
                AgentId = record.AgentId,
                Kind = record.Kind.ToString(),
                Key = record.Key,
                Value = record.Value,
                CreatedAt = record.CreatedAt,
                WorkspaceId = record.WorkspaceId
            });
            memoryId = record.MemoryId;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (!embeddings.IsConfigured || !await HasVectorColumnAsync(db, cancellationToken)) return;

        // Embedded after the row is saved: if the provider is down the entry still exists and
        // keyword search finds it; it just isn't found by meaning until it's written again.
        var vector = await embeddings.EmbedAsync($"{record.Key}\n{record.Value}", cancellationToken);
        if (vector is null) return;

        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "MemoryEntries" SET "Embedding" = CAST(@p0 AS vector), "EmbeddingModel" = @p1 WHERE "MemoryId" = @p2""",
            [VectorLiteral(vector), embeddings.Model, memoryId], cancellationToken);
    }

    public async Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        scope ??= MemoryScope.Organization;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.MemoryEntries
            .Where(m => m.TenantId == tenantId && m.Key == key && (m.AgentId == agentId || m.Kind == nameof(MemoryKind.Shared)))
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        // Its own entries always; shared ones only where the reader can see them.
        var entity = candidates.FirstOrDefault(m => m.AgentId == agentId || scope.Includes(m.WorkspaceId));
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string tenantId, string query, MemoryKind? kind = null, string? agentId = null, MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        scope ??= MemoryScope.Organization;
        query = (query ?? string.Empty).Trim();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        float[]? queryVector = null;
        var useVectors = query.Length > 0 && embeddings.IsConfigured && await HasVectorColumnAsync(db, cancellationToken);
        if (useVectors) queryVector = await embeddings.EmbedAsync(query, cancellationToken);
        useVectors &= queryVector is not null;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSearchSql(useVectors);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)kind?.ToString() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = (object?)agentId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("ws", NpgsqlDbType.Text) { Value = (object?)scope.WorkspaceId ?? DBNull.Value });
        command.Parameters.AddWithValue("wsonly", scope.WorkspaceOnly);
        command.Parameters.AddWithValue("q", query);
        command.Parameters.AddWithValue("like", "%" + EscapeLike(query) + "%");
        command.Parameters.AddWithValue("halflife", Math.Max(0.01, _search.RecencyHalfLifeDays));
        command.Parameters.AddWithValue("wv", _search.VectorWeight);
        command.Parameters.AddWithValue("wk", useVectors ? _search.KeywordWeight : _search.KeywordWeight + _search.VectorWeight);
        command.Parameters.AddWithValue("wr", _search.RecencyWeight);
        command.Parameters.AddWithValue("minsim", _search.MinSimilarity);
        command.Parameters.AddWithValue("limit", Math.Clamp(_search.MaxResults, 1, 200));
        if (useVectors)
        {
            command.Parameters.AddWithValue("emb", VectorLiteral(queryVector!));
            command.Parameters.AddWithValue("model", embeddings.Model);
        }

        var results = new List<MemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MemoryRecord
            {
                MemoryId = reader.GetString(0),
                TenantId = reader.GetString(1),
                AgentId = reader.GetString(2),
                Kind = Enum.Parse<MemoryKind>(reader.GetString(3)),
                Key = reader.GetString(4),
                Value = reader.GetString(5),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                Score = Math.Round(reader.GetDouble(7), 4),
                WorkspaceId = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return results;
    }

    /// <summary>
    /// One query: tenant (and kind/agent) filter first, then each candidate scored on meaning
    /// (cosine similarity, same model only), words (full-text rank, boosted for a literal match)
    /// and age. Entries that match neither by meaning nor by words are left out; an empty query
    /// lists the most recent.
    /// </summary>
    private static string BuildSearchSql(bool useVectors)
    {
        var vec = useVectors
            ? """CASE WHEN m."EmbeddingModel" = @model AND m."Embedding" IS NOT NULL THEN 1 - (m."Embedding" <=> CAST(@emb AS vector)) ELSE 0 END"""
            : "0";
        return $"""
            SELECT "MemoryId", "TenantId", "AgentId", "Kind", "Key", "Value", "CreatedAt",
                   (@wv * vec + @wk * LEAST(1.0, kw * 10 + CASE WHEN sub THEN 0.5 ELSE 0 END) + @wr * rec)::float8 AS score,
                   "WorkspaceId"
            FROM (
                SELECT m."MemoryId", m."TenantId", m."AgentId", m."Kind", m."Key", m."Value", m."CreatedAt", m."WorkspaceId",
                       ({vec})::float8 AS vec,
                       CASE WHEN @q = '' THEN 0 ELSE ts_rank_cd(to_tsvector('english', m."Key" || ' ' || m."Value"), websearch_to_tsquery('english', @q)) END::float8 AS kw,
                       (@q <> '' AND (m."Key" ILIKE @like OR m."Value" ILIKE @like)) AS sub,
                       EXP(-LN(2) * EXTRACT(EPOCH FROM (now() - m."CreatedAt")) / 86400.0 / @halflife)::float8 AS rec
                FROM "MemoryEntries" m
                WHERE m."TenantId" = @tenant
                  AND (@kind IS NULL OR m."Kind" = @kind)
                  AND (@agent IS NULL OR m."AgentId" = @agent)
                  AND (CASE
                         WHEN @ws IS NULL THEN m."WorkspaceId" IS NULL
                         WHEN @wsonly THEN m."WorkspaceId" = @ws
                         ELSE m."WorkspaceId" IS NULL OR m."WorkspaceId" = @ws
                       END)
            ) s
            WHERE @q = '' OR vec >= @minsim OR kw > 0 OR sub
            ORDER BY score DESC, "CreatedAt" DESC
            LIMIT @limit
            """;
    }

    /// <summary>Whether this database has pgvector and the Embedding column (checked once).</summary>
    private async Task<bool> HasVectorColumnAsync(AgentDbContext db, CancellationToken ct)
    {
        if (_vectorColumn is { } known) return known;

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MemoryEntries' AND column_name = 'Embedding')
            """;
        var exists = (bool)(await command.ExecuteScalarAsync(ct))!;
        if (!exists)
        {
            logger.LogWarning("Memory embeddings are configured ({Model}) but this Postgres has no pgvector extension; " +
                              "memory search uses keywords only. Use the pgvector/pgvector image to enable semantic recall.", embeddings.Model);
        }

        _vectorColumn = exists;
        return exists;
    }

    internal static string VectorLiteral(float[] vector)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return sb.Append(']').ToString();
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static MemoryRecord ToRecord(MemoryEntity e) => new()
    {
        MemoryId = e.MemoryId,
        TenantId = e.TenantId,
        AgentId = e.AgentId,
        Kind = Enum.Parse<MemoryKind>(e.Kind),
        Key = e.Key,
        Value = e.Value,
        CreatedAt = e.CreatedAt,
        WorkspaceId = e.WorkspaceId
    };
}
