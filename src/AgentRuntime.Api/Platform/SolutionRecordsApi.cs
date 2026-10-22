using System.Text.Json.Nodes;
using AgentRuntime.Solutions;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// The records API's behaviour (docs/solutions.md#your-backend), shared by its two doors: the
/// organization's API (<c>/api/solutions/{id}/records</c>, with an API key or a session) and the apps
/// API (<c>/api/apps/{installId}/records</c>, with the solution's app key). Same filters, the same
/// checks on every write, the same idempotency and concurrency rules.
/// </summary>
public sealed class SolutionRecordsApi(IRecordStore records)
{
    /// <summary>The entity's definition; an unknown entity is a 404. Writes need an active install:
    /// an uninstalled solution's records can still be read (and exported), not changed, since nothing
    /// would react to the change.</summary>
    public static EntityDefinition Entity(InstalledSolution solution, string entity, bool write = false)
    {
        if (!solution.Manifest.Entities.TryGetValue(entity, out var definition)) throw new SolutionException($"No entity '{entity}'.", StatusCodes.Status404NotFound);
        if (write && solution.Status != SolutionInstallStatus.Active) throw new SolutionException("The solution isn't installed any more; its records are read-only.", StatusCodes.Status409Conflict);
        return definition;
    }

    /// <summary>
    /// Lists records. Exact matches as <c>?where.status=open</c>, comparisons as
    /// <c>?filter=ends_on&lt;=today+30d</c> (repeatable; ops &lt; &lt;= &gt; &gt;= == != ~ for contains),
    /// plus order_by, desc, limit and offset. <c>?end_user=</c> narrows to one end user's records.
    /// </summary>
    public async Task<IActionResult> ListAsync(InstalledSolution solution, string entity, IQueryCollection query, CancellationToken ct)
    {
        var definition = Entity(solution, entity);
        var where = query.Where(q => q.Key.StartsWith("where.", StringComparison.Ordinal)).ToDictionary(q => q.Key[6..], q => q.Value.ToString());
        var filters = new List<WatchCondition>();
        foreach (var f in query["filter"])
        {
            if (ParseFilter(f) is not { } condition) return new BadRequestObjectResult(new { error = $"Can't read the filter '{f}' (e.g. rent>=1000, ends_on<=today+30d, name~smith)." });
            filters.Add(condition);
        }

        // Unreadable paging or order values are refused, not silently replaced by the defaults.
        var limit = 50;
        var offset = 0;
        var desc = true;
        if ((query["limit"].ToString() is { Length: > 0 } l && !int.TryParse(l, out limit)) ||
            (query["offset"].ToString() is { Length: > 0 } o && !int.TryParse(o, out offset)) ||
            (query["desc"].ToString() is { Length: > 0 } d && !bool.TryParse(d, out desc)))
        {
            return new BadRequestObjectResult(new { error = "limit and offset must be whole numbers, and desc true or false." });
        }

        try
        {
            var endUser = query["end_user"].ToString() is { Length: > 0 } u ? u : null;
            var (items, total) = await records.ListAsync(new RecordScope(solution.TenantId, solution.InstallId, endUser), entity, definition, new RecordQuery
            {
                Where = where,
                Filters = filters,
                OrderBy = query["order_by"].ToString() is { Length: > 0 } orderBy ? orderBy : null,
                Descending = desc,
                Limit = limit,
                Offset = offset
            }, ct);
            return new OkObjectResult(new { entity, total, items = items.Select(r => r.ToJson()) });
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
    }

    public static WatchCondition? ParseFilter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // The operator is the first one in the text (two-character ones win a tie), so a value may
        // itself contain operator characters: "name~a<b" is name contains "a<b".
        (int At, string Op)? found = null;
        foreach (var op in new[] { "<=", ">=", "!=", "==", "<", ">", "~", "=" })
        {
            var at = text.IndexOf(op, StringComparison.Ordinal);
            if (at > 0 && (found is null || at < found.Value.At)) found = (at, op);
        }

        if (found is { } f)
        {
            var field = text[..f.At].Trim();
            var value = text[(f.At + f.Op.Length)..].Trim();
            return new WatchCondition { Field = field, Op = f.Op switch { "~" => "contains", "=" => "==", _ => f.Op }, Value = value };
        }

        return text.EndsWith('?') ? new WatchCondition { Field = text.TrimEnd('?').Trim(), Op = "exists" } : null;
    }

    public async Task<IActionResult> GetAsync(InstalledSolution solution, string entity, string recordId, CancellationToken ct)
    {
        var definition = Entity(solution, entity);
        return await records.GetAsync(new RecordScope(solution.TenantId, solution.InstallId), entity, definition, recordId, ct) is { } record
            ? new OkObjectResult(record.ToJson())
            : new NotFoundObjectResult(new { error = $"No {entity} '{recordId}'." });
    }

    /// <summary>Creates a record (the body is its fields). An idempotency key makes a retried create
    /// return the record the first one made; <paramref name="keySpace"/> keeps two callers' keys apart.</summary>
    public async Task<IActionResult> CreateAsync(InstalledSolution solution, string entity, JsonObject fields, string? idempotencyKey, string keySpace, string writer,
        CancellationToken ct)
    {
        Entity(solution, entity, write: true);
        var recordId = string.IsNullOrWhiteSpace(idempotencyKey) ? null : SolutionIds.RecordFor($"{solution.InstallId}:{keySpace}:{idempotencyKey.Trim()}");
        return Written(await records.CreateAsync(new RecordScope(solution.TenantId, solution.InstallId), solution.Manifest, entity, fields,
            new RecordWriter(writer), recordId, ct), created: true);
    }

    /// <summary>Changes fields (null clears one). <paramref name="ifMatch"/> (a version) refuses the
    /// change if the record changed since.</summary>
    public async Task<IActionResult> UpdateAsync(InstalledSolution solution, string entity, string recordId, JsonObject fields, string? ifMatch, string writer,
        CancellationToken ct)
    {
        Entity(solution, entity, write: true);
        int? expected = int.TryParse(ifMatch?.Trim('"', ' ', 'W', '/'), out var v) ? v : null;
        return Written(await records.UpdateAsync(new RecordScope(solution.TenantId, solution.InstallId), solution.Manifest, entity, recordId, fields,
            new RecordWriter(writer), expected, ct));
    }

    public async Task<IActionResult> DeleteAsync(InstalledSolution solution, string entity, string recordId, string writer, CancellationToken ct)
    {
        Entity(solution, entity, write: true);
        var result = await records.DeleteAsync(new RecordScope(solution.TenantId, solution.InstallId), solution.Manifest, entity, recordId, new RecordWriter(writer), ct);
        return result.Success ? new NoContentResult() : Written(result);
    }

    private static IActionResult Written(RecordWriteResult result, bool created = false) =>
        result.Success ? created && !result.Replayed ? new ObjectResult(result.Record!.ToJson()) { StatusCode = StatusCodes.Status201Created } : new OkObjectResult(result.Record!.ToJson())
        : result.NotFound ? new NotFoundObjectResult(new { error = result.Errors.FirstOrDefault(), errors = result.Errors })
        : result.Conflict ? new ConflictObjectResult(new { error = result.Errors.FirstOrDefault(), errors = result.Errors })
        : new UnprocessableEntityObjectResult(new { error = string.Join(" ", result.Errors), errors = result.Errors });
}
