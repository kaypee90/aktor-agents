using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Where an organization's tokens and money go and what takes long (docs/analytics.md): totals
/// against the previous period, a trend, spend by agent role and source, tool timings, run
/// durations, and the runs behind the numbers. Filters: a preset range or from/to, source,
/// status and a goal search. Runs count in the period they started in.
/// </summary>
[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController(AgentDbContext db, TenantAccess access) : ControllerBase
{
    private static readonly string[] FailedStatuses = ["Failed", "TimedOut", "Rejected"];
    private const int MaxRuns = 20_000;
    private const int MaxToolSamples = 100_000;

    private static readonly (string Label, double UpTo)[] DurationBuckets =
    [
        ("< 30s", 30), ("30s–1m", 60), ("1–2m", 120), ("2–5m", 300), ("5–10m", 600), ("10–30m", 1800), ("30m+", double.MaxValue)
    ];

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? range,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? source,
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromQuery(Name = "tz_offset_minutes")] int tzOffsetMinutes,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var window = range switch
        {
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            "90d" => TimeSpan.FromDays(90),
            null or "" => (TimeSpan?)null,
            _ => TimeSpan.Zero
        };
        if (window == TimeSpan.Zero) return BadRequest(new { error = "range is one of 24h, 7d, 30d, 90d." });

        var end = window is null ? (to ?? now) : now;
        var start = window is { } w ? now - w : from ?? end - TimeSpan.FromDays(7);
        if (start >= end) return BadRequest(new { error = "from must be before to." });
        if (end - start > TimeSpan.FromDays(400)) return BadRequest(new { error = "Choose a range of at most 400 days." });
        if (status is not (null or "" or "running" or "completed" or "failed")) return BadRequest(new { error = "status is running, completed or failed." });

        var tenant = access.TenantId;
        var runs = Filtered(tenant, start, end, source, status, q);
        var tasks = await runs
            .OrderByDescending(t => t.CreatedAt)
            .Take(MaxRuns)
            .Select(t => new { t.TaskId, t.Goal, t.Status, t.Source, t.CreatedAt, t.CompletedAt })
            .ToListAsync(ct);
        var ids = runs.Select(t => t.TaskId);

        var agents = db.Agents.AsNoTracking().Where(a => a.TenantId == tenant && ids.Contains(a.TaskId));
        var perTask = (await agents
                .GroupBy(a => a.TaskId)
                .Select(g => new { TaskId = g.Key, Tokens = g.Sum(a => (long)a.TokensUsed), Cost = g.Sum(a => a.CostUsd), Agents = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.TaskId);
        var byRole = await agents
            .GroupBy(a => a.Role)
            .Select(g => new { Role = g.Key, Agents = g.Count(), Tokens = g.Sum(a => (long)a.TokensUsed), Cost = g.Sum(a => a.CostUsd) })
            .OrderByDescending(x => x.Tokens)
            .Take(25)
            .ToListAsync(ct);

        var toolCalls = db.ToolCalls.AsNoTracking().Where(c => c.TenantId == tenant && ids.Contains(c.TaskId));
        var byTool = await toolCalls
            .GroupBy(c => c.ToolName)
            .Select(g => new
            {
                Tool = g.Key,
                Calls = g.Count(),
                Failures = g.Count(c => !c.Success),
                Avg = g.Average(c => (double?)c.DurationMs),
                Total = g.Sum(c => (long?)c.DurationMs) ?? 0
            })
            .OrderByDescending(x => x.Total)
            .Take(25)
            .ToListAsync(ct);
        var samples = (await toolCalls
                .Where(c => c.DurationMs != null)
                .OrderByDescending(c => c.Id)
                .Take(MaxToolSamples)
                .Select(c => new { c.ToolName, Ms = c.DurationMs!.Value })
                .ToListAsync(ct))
            .GroupBy(s => s.ToolName)
            .ToDictionary(g => g.Key, g => Percentile(g.Select(s => (double)s.Ms).ToList(), 0.95));

        // One row per run, with its spend.
        var rows = tasks.Select(t =>
        {
            perTask.TryGetValue(t.TaskId, out var usage);
            return new Row(t.TaskId, t.Goal, t.Status, t.Source, t.CreatedAt, t.CompletedAt is { } done ? (done - t.CreatedAt).TotalSeconds : null,
                usage?.Tokens ?? 0, usage?.Cost ?? 0, usage?.Agents ?? 0);
        }).ToList();
        var finished = rows.Where(r => r.DurationS is not null).Select(r => r.DurationS!.Value).ToList();

        var previous = await PreviousAsync(tenant, start - (end - start), start, source, status, q, ct);
        var offset = TimeSpan.FromMinutes(Math.Clamp(-tzOffsetMinutes, -14 * 60, 14 * 60));
        var hourly = end - start <= TimeSpan.FromDays(2);

        return Ok(new
        {
            range = new { from = start, to = end, bucket = hourly ? "hour" : "day" },
            truncated = tasks.Count == MaxRuns,
            totals = new
            {
                runs = rows.Count,
                completed = rows.Count(r => r.DurationS is not null && !FailedStatuses.Contains(r.Status)),
                failed = rows.Count(r => r.DurationS is not null && FailedStatuses.Contains(r.Status)),
                running = rows.Count(r => r.DurationS is null),
                tokens = rows.Sum(r => r.Tokens),
                cost_usd = rows.Sum(r => r.Cost),
                avg_cost_usd = rows.Count == 0 ? 0 : rows.Average(r => r.Cost),
                avg_tokens = rows.Count == 0 ? 0 : rows.Average(r => r.Tokens),
                avg_duration_s = finished.Count == 0 ? (double?)null : finished.Average(),
                p50_duration_s = finished.Count == 0 ? (double?)null : Percentile(finished, 0.5),
                p95_duration_s = finished.Count == 0 ? (double?)null : Percentile(finished, 0.95),
                agents = rows.Sum(r => r.Agents),
                tool_calls = byTool.Sum(t => t.Calls),
                tool_failures = byTool.Sum(t => t.Failures)
            },
            previous,
            series = Series(rows, start, end, hourly, offset),
            by_role = byRole.Select(r => new
            {
                role = r.Role,
                agents = r.Agents,
                tokens = r.Tokens,
                cost_usd = r.Cost,
                avg_tokens = r.Agents == 0 ? 0 : r.Tokens / (double)r.Agents
            }),
            by_source = rows.GroupBy(r => r.Source).Select(g => new { source = g.Key, runs = g.Count(), tokens = g.Sum(r => r.Tokens), cost_usd = g.Sum(r => r.Cost) })
                .OrderByDescending(x => x.cost_usd),
            by_status = rows.GroupBy(r => r.DurationS is null ? "Running" : r.Status).Select(g => new { status = g.Key, runs = g.Count() }),
            by_tool = byTool.Select(t => new
            {
                tool = t.Tool,
                calls = t.Calls,
                failures = t.Failures,
                avg_duration_ms = t.Avg,
                p95_duration_ms = samples.TryGetValue(t.Tool, out var p95) ? p95 : (double?)null,
                total_duration_ms = t.Total
            }),
            duration_histogram = DurationBuckets.Select((b, i) => new
            {
                label = b.Label,
                runs = finished.Count(d => d < b.UpTo && (i == 0 || d >= DurationBuckets[i - 1].UpTo))
            }),
            top_by_cost = rows.OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(10).Select(View),
            slowest = rows.Where(r => r.DurationS is not null).OrderByDescending(r => r.DurationS).Take(10).Select(View)
        });
    }

    private sealed record Row(string TaskId, string Goal, string Status, string Source, DateTimeOffset CreatedAt, double? DurationS, long Tokens, decimal Cost, int Agents);

    private static object View(Row r) => new
    {
        task_id = r.TaskId,
        goal = r.Goal,
        status = r.DurationS is null ? "Running" : r.Status,
        source = r.Source,
        created_at = r.CreatedAt,
        duration_s = r.DurationS,
        tokens = r.Tokens,
        cost_usd = r.Cost,
        agents = r.Agents
    };

    private IQueryable<TaskRecord> Filtered(string tenant, DateTimeOffset start, DateTimeOffset end, string? source, string? status, string? q)
    {
        var runs = db.Tasks.AsNoTracking().Where(t => t.TenantId == tenant && t.CreatedAt >= start && t.CreatedAt < end);
        if (!string.IsNullOrWhiteSpace(source)) runs = runs.Where(t => t.Source == source);
        runs = status switch
        {
            "running" => runs.Where(t => t.CompletedAt == null),
            "failed" => runs.Where(t => t.CompletedAt != null && FailedStatuses.Contains(t.Status)),
            "completed" => runs.Where(t => t.CompletedAt != null && !FailedStatuses.Contains(t.Status)),
            _ => runs
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            runs = runs.Where(t => EF.Functions.ILike(t.Goal, pattern));
        }

        return runs;
    }

    /// <summary>The same filters over the period just before, for the "vs previous" figures.</summary>
    private async Task<object> PreviousAsync(string tenant, DateTimeOffset start, DateTimeOffset end, string? source, string? status, string? q, CancellationToken ct)
    {
        var runs = Filtered(tenant, start, end, source, status, q);
        var ids = runs.Select(t => t.TaskId);
        var count = await runs.CountAsync(ct);
        var agents = db.Agents.AsNoTracking().Where(a => a.TenantId == tenant && ids.Contains(a.TaskId));
        var tokens = await agents.SumAsync(a => (long)a.TokensUsed, ct);
        var cost = await agents.SumAsync(a => a.CostUsd, ct);
        var durations = await runs.Where(t => t.CompletedAt != null)
            .Select(t => new { t.CreatedAt, t.CompletedAt })
            .Take(MaxRuns)
            .ToListAsync(ct);
        return new
        {
            runs = count,
            tokens,
            cost_usd = cost,
            avg_cost_usd = count == 0 ? 0 : cost / count,
            avg_duration_s = durations.Count == 0 ? (double?)null : durations.Average(d => (d.CompletedAt!.Value - d.CreatedAt).TotalSeconds)
        };
    }

    /// <summary>One point per hour or (local) day across the whole range, empty ones included.</summary>
    private static IEnumerable<object> Series(List<Row> rows, DateTimeOffset start, DateTimeOffset end, bool hourly, TimeSpan offset)
    {
        DateTimeOffset Bucket(DateTimeOffset t)
        {
            var local = t.ToOffset(offset);
            var floor = hourly
                ? new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, offset)
                : new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, offset);
            return floor.ToUniversalTime();
        }

        var step = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var groups = rows.GroupBy(r => Bucket(r.CreatedAt)).ToDictionary(g => g.Key);
        for (var t = Bucket(start); t < end; t += step)
        {
            groups.TryGetValue(t, out var g);
            var done = g?.Where(r => r.DurationS is not null).Select(r => r.DurationS!.Value).ToList() ?? [];
            yield return new
            {
                t,
                runs = g?.Count() ?? 0,
                tokens = g?.Sum(r => r.Tokens) ?? 0,
                cost_usd = g?.Sum(r => r.Cost) ?? 0,
                avg_duration_s = done.Count == 0 ? (double?)null : done.Average()
            };
        }
    }

    private static double Percentile(List<double> values, double p)
    {
        values.Sort();
        var rank = p * (values.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return values[lower] + (values[upper] - values[lower]) * (rank - lower);
    }
}
