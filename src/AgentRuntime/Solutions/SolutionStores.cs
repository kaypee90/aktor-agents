using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Solutions;

/// <summary>One record as stored.</summary>
public sealed record SolutionRecord
{
    public required string RecordId { get; init; }
    public required string Entity { get; init; }
    public required JsonObject Data { get; init; }
    public int Version { get; init; }
    public string? EndUserId { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The record as agents and apps see it: its fields plus id, version and times.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["id"] = RecordId };
        foreach (var (k, v) in Data) json[k] = v?.DeepClone();
        json["version"] = Version;
        json["created_at"] = CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        json["updated_at"] = UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        return json;
    }
}

/// <summary>Whose records a call can reach: one organization's install, and, for an end user's
/// conversation, only that end user's records of entities that have an end_user field.</summary>
public sealed record RecordScope(string TenantId, string InstallId, string? EndUserId = null);

public sealed record RecordQuery
{
    /// <summary>Fields equal to these values (pushed down to the database).</summary>
    public Dictionary<string, string> Where { get; init; } = [];
    /// <summary>Other comparisons (&lt;, &gt;=, contains, exists, …), as in watches. Values may be
    /// <c>today</c>, <c>today+30d</c>, <c>today-7d</c> or <c>now</c>.</summary>
    public List<WatchCondition> Filters { get; init; } = [];
    /// <summary>A field, or created_at / updated_at (the default, newest first).</summary>
    public string? OrderBy { get; init; }
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

public sealed record RecordWriteResult
{
    public bool Success { get; init; }
    public SolutionRecord? Record { get; init; }
    public List<string> Errors { get; init; } = [];
    public bool NotFound { get; init; }
    /// <summary>The record changed since the version the writer read.</summary>
    public bool Conflict { get; init; }
    /// <summary>A replayed create found the record it already made.</summary>
    public bool Replayed { get; init; }

    public static RecordWriteResult Ok(SolutionRecord record, bool replayed = false) => new() { Success = true, Record = record, Replayed = replayed };
    public static RecordWriteResult Fail(params string[] errors) => new() { Errors = [.. errors] };
    public static RecordWriteResult Missing(string entity, string id) => new() { NotFound = true, Errors = [$"No {entity} '{id}'."] };
}

/// <summary>Who's writing, for the record's history and the change it emits.</summary>
public sealed record RecordWriter(string By, string? TaskId = null);

/// <summary>
/// Solution records (docs/solutions.md#data). Every method is scoped by <see cref="RecordScope"/>,
/// which the runtime sets (never an agent or end user), and every write validates against the
/// entity's definition and writes its change to the outbox in the same transaction.
/// </summary>
public interface IRecordStore
{
    Task<(IReadOnlyList<SolutionRecord> Items, int Total)> ListAsync(RecordScope scope, string entity, EntityDefinition definition, RecordQuery query,
        CancellationToken cancellationToken = default);

    Task<SolutionRecord?> GetAsync(RecordScope scope, string entity, EntityDefinition definition, string recordId, CancellationToken cancellationToken = default);

    /// <summary>With <paramref name="recordId"/> fixed (from a tool call's idempotency key), a
    /// repeated create returns the record it already made.</summary>
    Task<RecordWriteResult> CreateAsync(RecordScope scope, SolutionManifest manifest, string entity, JsonObject data, RecordWriter writer,
        string? recordId = null, CancellationToken cancellationToken = default);

    Task<RecordWriteResult> UpdateAsync(RecordScope scope, SolutionManifest manifest, string entity, string recordId, JsonObject changes, RecordWriter writer,
        int? expectedVersion = null, CancellationToken cancellationToken = default);

    Task<RecordWriteResult> DeleteAsync(RecordScope scope, SolutionManifest manifest, string entity, string recordId, RecordWriter writer,
        CancellationToken cancellationToken = default);

    /// <summary>Records of <paramref name="entity"/> created or updated by the given tasks (their
    /// latest state), for evals.</summary>
    Task<IReadOnlyList<JsonObject>> ChangedByTasksAsync(string tenantId, string installId, string entity, IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default);
}

/// <summary>An install as the runtime reads it.</summary>
public sealed record InstalledSolution
{
    public required string InstallId { get; init; }
    public required string TenantId { get; init; }
    public required SolutionManifest Manifest { get; init; }
    /// <summary>Workspace key → workspace id.</summary>
    public IReadOnlyDictionary<string, string> Workspaces { get; init; } = new Dictionary<string, string>();
    public SolutionInstallStatus Status { get; init; }
    public string Version => Manifest.Version;
}

/// <summary>Installed solutions by id (cached; an upgrade invalidates).</summary>
public interface ISolutionCatalog
{
    Task<InstalledSolution?> GetAsync(string tenantId, string installId, CancellationToken cancellationToken = default);

    /// <summary>An install by id whatever its organization: only for the runtime to find which
    /// organization a request (a federated end-user token) is for, never for showing it to a caller.</summary>
    Task<InstalledSolution?> FindAsync(string installId, CancellationToken cancellationToken = default);

    void Invalidate(string installId);
}

/// <summary>Relative dates in record filters: today, today+30d, today-7d, now.</summary>
public static partial class RelativeDates
{
    [GeneratedRegex(@"^\s*(today|now)\s*(?:([+-])\s*(\d{1,4})\s*([dhw]?))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static string Resolve(string? value, DateTimeOffset now)
    {
        if (value is null || Pattern().Match(value) is not { Success: true } m) return value ?? string.Empty;
        var isToday = m.Groups[1].Value.Equals("today", StringComparison.OrdinalIgnoreCase);
        var at = isToday ? new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) : now.ToUniversalTime();
        if (m.Groups[2].Success)
        {
            var n = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value == "-" ? -1 : 1);
            at = m.Groups[4].Value.ToLowerInvariant() switch
            {
                "h" => at.AddHours(n),
                "w" => at.AddDays(7 * n),
                _ => at.AddDays(n)
            };
        }

        return isToday ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : at.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
