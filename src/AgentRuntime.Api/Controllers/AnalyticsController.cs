using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Where an organization's tokens and money go and what takes long (docs/analytics.md): totals
/// against the previous period, a trend, spend by agent role and source, tool timings, run
/// durations, usage by user, and the runs behind the numbers. Filters: a preset range or from/to,
/// source, status, who started the run and a goal search. Runs count in the period they started in.
/// </summary>
[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController(AgentDbContext db, TenantAccess access) : ControllerBase
{
    private static readonly string[] FailedStatuses = ["Failed", "TimedOut", "Rejected"];
    private const int MaxRuns = 20_000;
    private const int MaxToolSamples = 100_000;
    /// <summary>The <c>user</c> filter value for runs with no recorded starter (from before it was recorded).</summary>
    private const string UnknownUser = "unknown";

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
        [FromQuery] string? scope,
        [FromQuery] string? workspace,
        [FromQuery] string? model,
        [FromQuery] string? user,
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
        if (scope is not (null or "" or "tasks" or "workspaces")) return BadRequest(new { error = "scope is tasks or workspaces." });

        var offset = TimeSpan.FromMinutes(Math.Clamp(-tzOffsetMinutes, -14 * 60, 14 * 60));
        if (scope == "workspaces") return Ok(await WorkspacesAsync(start, end, workspace, model, offset, ct));

        var tenant = access.TenantId;
        var runs = Filtered(tenant, start, end, source, status, q, model, user);
        var tasks = await runs
            .OrderByDescending(t => t.CreatedAt)
            .Take(MaxRuns)
            .Select(t => new { t.TaskId, t.Goal, t.Status, t.Source, t.CreatedAt, t.CompletedAt, t.StartedBy })
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
                usage?.Tokens ?? 0, usage?.Cost ?? 0, usage?.Agents ?? 0, t.StartedBy ?? UnknownUser);
        }).ToList();
        var finished = rows.Where(r => r.DurationS is not null).Select(r => r.DurationS!.Value).ToList();

        var previous = await PreviousAsync(tenant, start - (end - start), start, source, status, q, model, user, ct);
        var people = await PeopleAsync(tenant, rows.Select(r => r.StartedBy), ct);
        object View(Row r) => new
        {
            task_id = r.TaskId,
            goal = r.Goal,
            status = r.DurationS is null ? "Running" : r.Status,
            source = r.Source,
            created_at = r.CreatedAt,
            duration_s = r.DurationS,
            tokens = r.Tokens,
            cost_usd = r.Cost,
            agents = r.Agents,
            started_by = r.StartedBy,
            started_by_name = people[r.StartedBy].Name
        };
        var byModel = await ByModelAsync(db.LlmCalls.AsNoTracking().Where(c => c.TenantId == tenant && ids.Contains(c.TaskId)), ct);
        var hourly = end - start <= TimeSpan.FromDays(2);

        return Ok(new
        {
            scope = "tasks",
            range = new { from = start, to = end, bucket = hourly ? "hour" : "day" },
            truncated = tasks.Count == MaxRuns,
            by_model = byModel,
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
            by_user = rows.GroupBy(r => r.StartedBy).Select(g => new
                {
                    user = g.Key,
                    name = people[g.Key].Name,
                    kind = people[g.Key].Kind,
                    runs = g.Count(),
                    tokens = g.Sum(r => r.Tokens),
                    cost_usd = g.Sum(r => r.Cost),
                    avg_cost_usd = g.Average(r => r.Cost),
                    failed = g.Count(r => r.DurationS is not null && FailedStatuses.Contains(r.Status)),
                    last_run_at = g.Max(r => r.CreatedAt)
                })
                .OrderByDescending(x => x.cost_usd).ThenByDescending(x => x.runs)
                .Take(50),
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

    private sealed record Row(string TaskId, string Goal, string Status, string Source, DateTimeOffset CreatedAt, double? DurationS, long Tokens, decimal Cost, int Agents,
        string StartedBy);

    private sealed record Person(string Name, string Kind);

    /// <summary>
    /// Display names for the starters of runs: a member's name or email, an API key's name, "Local"
    /// when sign-in is off, "Triggers" for pipeline runs a trigger started, and "Not recorded" for
    /// runs from before starters were recorded. A user
    /// or key that has since been deleted keeps a recognisable short id.
    /// </summary>
    private async Task<Dictionary<string, Person>> PeopleAsync(string tenant, IEnumerable<string> starters, CancellationToken ct)
    {
        var ids = starters.Distinct().ToList();
        var userIds = ids.Where(id => id is not (UnknownUser or "local" or "trigger") && !id.StartsWith("key:", StringComparison.Ordinal)).ToList();
        var keyIds = ids.Where(id => id.StartsWith("key:", StringComparison.Ordinal)).Select(id => id[4..]).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.Name, u.Email }).ToDictionaryAsync(u => u.UserId, ct);
        var keys = await db.ApiKeys.AsNoTracking().Where(k => k.TenantId == tenant && keyIds.Contains(k.KeyId))
            .Select(k => new { k.KeyId, k.Name }).ToDictionaryAsync(k => k.KeyId, k => k.Name, ct);

        return ids.ToDictionary(id => id, id => id switch
        {
            UnknownUser => new Person("Not recorded", "unknown"),
            "local" => new Person("Local (sign-in off)", "user"),
            "trigger" => new Person("Triggers (automatic runs)", "trigger"),
            _ when id.StartsWith("key:", StringComparison.Ordinal) =>
                new Person(keys.TryGetValue(id[4..], out var key) ? $"API key · {key}" : $"API key · {Short(id[4..])} (deleted)", "api_key"),
            _ => users.TryGetValue(id, out var u)
                ? new Person(string.IsNullOrWhiteSpace(u.Name) ? u.Email : $"{u.Name} ({u.Email})", "user")
                : new Person($"Former member · {Short(id)}", "user")
        });

        static string Short(string id) => id.Length <= 8 ? id : id[..8];
    }

    private IQueryable<TaskRecord> Filtered(string tenant, DateTimeOffset start, DateTimeOffset end, string? source, string? status, string? q, string? model,
        string? user)
    {
        var runs = db.Tasks.AsNoTracking().Where(t => t.TenantId == tenant && t.CreatedAt >= start && t.CreatedAt < end);
        if (!string.IsNullOrWhiteSpace(source)) runs = runs.Where(t => t.Source == source);
        if (user == UnknownUser) runs = runs.Where(t => t.StartedBy == null);
        else if (!string.IsNullOrWhiteSpace(user)) runs = runs.Where(t => t.StartedBy == user);
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

        // Runs that used the model for at least one call (a run can switch models part-way).
        if (!string.IsNullOrWhiteSpace(model))
        {
            runs = runs.Where(t => db.LlmCalls.Any(c => c.TaskId == t.TaskId && c.ProfileId == model));
        }

        return runs;
    }

    /// <summary>The same filters over the period just before, for the "vs previous" figures.</summary>
    private async Task<object> PreviousAsync(string tenant, DateTimeOffset start, DateTimeOffset end, string? source, string? status, string? q, string? model,
        string? user, CancellationToken ct)
    {
        var runs = Filtered(tenant, start, end, source, status, q, model, user);
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

    /// <summary>Spend and response times per model (profile and model id): which model is cheaper
    /// or faster for the same work. Only calls made since model calls are recorded count.</summary>
    private static async Task<List<object>> ByModelAsync(IQueryable<LlmCallRecord> calls, CancellationToken ct)
    {
        var rows = await calls
            .GroupBy(c => new { c.ProfileId, c.ProfileName, c.Provider, c.Model })
            .Select(g => new
            {
                g.Key.ProfileId,
                g.Key.ProfileName,
                g.Key.Provider,
                g.Key.Model,
                Calls = g.Count(),
                Tokens = g.Sum(c => (long)c.InputTokens + c.OutputTokens),
                Cost = g.Sum(c => c.CostUsd),
                AvgMs = g.Average(c => (double)c.DurationMs)
            })
            .OrderByDescending(x => x.Cost)
            .Take(20)
            .ToListAsync(ct);
        var samples = (await calls.OrderByDescending(c => c.Id).Take(MaxToolSamples)
                .Select(c => new { c.ProfileId, c.Model, c.DurationMs }).ToListAsync(ct))
            .GroupBy(c => (c.ProfileId, c.Model))
            .ToDictionary(g => g.Key, g => Percentile(g.Select(c => (double)c.DurationMs).ToList(), 0.95));
        return rows.Select(r => (object)new
        {
            profile_id = r.ProfileId,
            profile_name = r.ProfileName,
            provider = r.Provider,
            model = r.Model,
            label = r.ProfileName is { Length: > 0 } n && n != r.Model ? $"{n} · {r.Model}" : r.Model,
            calls = r.Calls,
            tokens = r.Tokens,
            cost_usd = r.Cost,
            avg_cost_per_call_usd = r.Calls == 0 ? 0 : r.Cost / r.Calls,
            avg_duration_ms = r.AvgMs,
            p95_duration_ms = samples.TryGetValue((r.ProfileId, r.Model), out var p95) ? p95 : (double?)null
        }).ToList();
    }

    /// <summary>
    /// Workspaces: their model calls (tokens, spend, time), tools, triggers that fired and approvals,
    /// per workspace, role and model, within the range, counted when it happened (a pipeline run spans
    /// its stages, so per-run timing is in the Tasks view). Model calls are recorded from this release on.
    /// </summary>
    private async Task<object> WorkspacesAsync(DateTimeOffset start, DateTimeOffset end, string? workspaceFilter, string? modelFilter, TimeSpan offset, CancellationToken ct)
    {
        var tenant = access.TenantId;
        var workspaces = await db.Workspaces.AsNoTracking().Where(w => w.TenantId == tenant)
            .Select(w => new { w.WorkspaceId, w.Name, w.Status }).ToListAsync(ct);
        var ids = workspaces.Select(w => w.WorkspaceId).Where(id => string.IsNullOrEmpty(workspaceFilter) || id == workspaceFilter).ToList();

        IQueryable<LlmCallRecord> Calls(DateTimeOffset from, DateTimeOffset to)
        {
            var q = db.LlmCalls.AsNoTracking().Where(c => c.TenantId == tenant && c.WorkspaceId != null && ids.Contains(c.WorkspaceId)
                                                          && c.Timestamp >= from && c.Timestamp < to);
            return string.IsNullOrEmpty(modelFilter) ? q : q.Where(c => c.ProfileId == modelFilter);
        }

        var calls = Calls(start, end);
        var rows = await calls.OrderByDescending(c => c.Id).Take(MaxToolSamples)
            .Select(c => new { c.WorkspaceId, c.Role, c.Timestamp, Tokens = (long)c.InputTokens + c.OutputTokens, c.CostUsd, c.DurationMs })
            .ToListAsync(ct);
        var durations = rows.Select(r => (double)r.DurationMs).ToList();

        // A pipeline's tools are called by its runs' agents, whose task is the run.
        var scopes = ids.Concat(await db.Tasks.AsNoTracking()
            .Where(t => t.TenantId == tenant && t.WorkspaceId != null && ids.Contains(t.WorkspaceId))
            .Select(t => t.TaskId).ToListAsync(ct)).ToList();
        var tools = await db.ToolCalls.AsNoTracking()
            .Where(c => c.TenantId == tenant && scopes.Contains(c.TaskId) && c.Timestamp >= start && c.Timestamp < end)
            .GroupBy(c => c.ToolName)
            .Select(g => new { Tool = g.Key, Calls = g.Count(), Failures = g.Count(c => !c.Success), Avg = g.Average(c => (double?)c.DurationMs), Total = g.Sum(c => (long?)c.DurationMs) ?? 0 })
            .OrderByDescending(x => x.Total)
            .Take(25)
            .ToListAsync(ct);

        var triggers = await db.Events.AsNoTracking()
            .Where(e => e.TenantId == tenant && e.Type == "TriggerFired" && e.TaskId != null && ids.Contains(e.TaskId) && e.Timestamp >= start && e.Timestamp < end)
            .GroupBy(e => e.TaskId!)
            .Select(g => new { WorkspaceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WorkspaceId, x => x.Count, ct);

        var approvals = await db.AuditEntries.AsNoTracking()
            .Where(a => ids.Contains(a.Scope) && a.Action.StartsWith("approval.") && a.At >= start && a.At < end)
            .GroupBy(a => new { a.Scope, a.Action })
            .Select(g => new { g.Key.Scope, g.Key.Action, Count = g.Count() })
            .ToListAsync(ct);
        int Approvals(string action, string? workspaceId = null) =>
            approvals.Where(a => a.Action == action && (workspaceId is null || a.Scope == workspaceId)).Sum(a => a.Count);

        var previous = await Calls(start - (end - start), start)
            .GroupBy(_ => 1)
            .Select(g => new { Calls = g.Count(), Tokens = g.Sum(c => (long)c.InputTokens + c.OutputTokens), Cost = g.Sum(c => c.CostUsd) })
            .FirstOrDefaultAsync(ct);

        var hourly = end - start <= TimeSpan.FromDays(2);
        var step = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        DateTimeOffset Bucket(DateTimeOffset t)
        {
            var local = t.ToOffset(offset);
            return (hourly
                ? new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, offset)
                : new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, offset)).ToUniversalTime();
        }

        var buckets = rows.GroupBy(r => Bucket(r.Timestamp)).ToDictionary(g => g.Key);
        var series = new List<object>();
        for (var t = Bucket(start); t < end; t += step)
        {
            buckets.TryGetValue(t, out var g);
            series.Add(new
            {
                t,
                calls = g?.Count() ?? 0,
                tokens = g?.Sum(r => r.Tokens) ?? 0,
                cost_usd = g?.Sum(r => r.CostUsd) ?? 0,
                avg_duration_s = g is null || !g.Any() ? (double?)null : g.Average(r => r.DurationMs) / 1000.0
            });
        }

        var names = workspaces.ToDictionary(w => w.WorkspaceId);
        var perWorkspace = rows.GroupBy(r => r.WorkspaceId!).ToDictionary(g => g.Key);
        return new
        {
            scope = "workspaces",
            range = new { from = start, to = end, bucket = hourly ? "hour" : "day" },
            truncated = rows.Count == MaxToolSamples,
            totals = new
            {
                workspaces = ids.Count,
                active_workspaces = perWorkspace.Count,
                calls = rows.Count,
                tokens = rows.Sum(r => r.Tokens),
                cost_usd = rows.Sum(r => r.CostUsd),
                avg_cost_per_day_usd = rows.Sum(r => r.CostUsd) / (decimal)Math.Max(1, (end - start).TotalDays),
                avg_call_ms = durations.Count == 0 ? (double?)null : durations.Average(),
                p95_call_ms = durations.Count == 0 ? (double?)null : Percentile(durations, 0.95),
                triggers_fired = triggers.Values.Sum(),
                approvals_requested = Approvals("approval.requested"),
                approvals_approved = Approvals("approval.approved"),
                approvals_rejected = Approvals("approval.rejected"),
                approvals_expired = Approvals("approval.expired"),
                tool_calls = tools.Sum(t => t.Calls),
                tool_failures = tools.Sum(t => t.Failures)
            },
            previous = new { calls = previous?.Calls ?? 0, tokens = previous?.Tokens ?? 0, cost_usd = previous?.Cost ?? 0 },
            series,
            by_workspace = ids.Select(id =>
            {
                perWorkspace.TryGetValue(id, out var g);
                return new
                {
                    workspace_id = id,
                    name = names[id].Name,
                    status = names[id].Status,
                    calls = g?.Count() ?? 0,
                    tokens = g?.Sum(r => r.Tokens) ?? 0,
                    cost_usd = g?.Sum(r => r.CostUsd) ?? 0,
                    triggers_fired = triggers.GetValueOrDefault(id),
                    approvals_requested = Approvals("approval.requested", id)
                };
            }).OrderByDescending(w => w.cost_usd).ThenByDescending(w => w.triggers_fired).ToList(),
            by_role = rows.GroupBy(r => r.Role).Select(g => new
            {
                role = g.Key,
                calls = g.Count(),
                tokens = g.Sum(r => r.Tokens),
                cost_usd = g.Sum(r => r.CostUsd),
                avg_tokens = g.Average(r => (double)r.Tokens)
            }).OrderByDescending(x => x.tokens).Take(25).ToList(),
            by_model = await ByModelAsync(calls, ct),
            by_tool = tools.Select(t => new
            {
                tool = t.Tool,
                calls = t.Calls,
                failures = t.Failures,
                avg_duration_ms = t.Avg,
                p95_duration_ms = (double?)null,
                total_duration_ms = t.Total
            })
        };
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
