using System.Globalization;
using System.Text;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Solutions;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// What a solution costs, broken down the way a builder bills (docs/solutions.md#usage-and-billing):
/// by end user (their conversations), by workspace (its runs) and by day. Costs are the runtime's own
/// metering of every agent's tokens and dollars, the same numbers the organization's plan is
/// metered by, so a builder can pass them on to their customers.
/// </summary>
public sealed class SolutionUsageService(IDbContextFactory<AgentDbContext> dbFactory, TimeProvider clock)
{
    public sealed record UsageLine(string Key, string? Label, int Conversations, int Runs, long Tokens, decimal CostUsd);

    public sealed record Usage(DateTimeOffset From, DateTimeOffset To, string By, long Tokens, decimal CostUsd, int Conversations, int Runs, List<UsageLine> Lines);

    /// <summary>The start of the current calendar month (UTC).</summary>
    public DateTimeOffset MonthStart()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public async Task<Usage> GetAsync(SolutionInstallRecord install, DateTimeOffset? from, DateTimeOffset? to, string by, CancellationToken ct)
    {
        var start = from ?? MonthStart();
        var end = to ?? clock.GetUtcNow();
        if (end <= start) throw new SolutionException("'to' must be after 'from'.");
        if (end - start > TimeSpan.FromDays(400)) throw new SolutionException("A usage report covers at most 400 days.");
        by = by.ToLowerInvariant() switch { "workspace" => "workspace", "day" => "day", _ => "end_user" };

        var workspaces = EfSolutionCatalog.ToSolution(install).Workspaces;
        var workspaceIds = workspaces.Values.ToList();
        var keyOf = workspaces.ToDictionary(w => w.Value, w => w.Key);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Each piece of work: an end user's conversation (a task) or a workspace run (a task too).
        var conversations = await db.SolutionConversations.AsNoTracking()
            .Where(c => c.TenantId == install.TenantId && c.InstallId == install.InstallId && c.CreatedAt >= start && c.CreatedAt < end)
            .Select(c => new { c.TaskId, c.EndUserId, c.CreatedAt })
            .ToListAsync(ct);
        var runs = await db.Tasks.AsNoTracking()
            .Where(t => t.TenantId == install.TenantId && t.WorkspaceId != null && workspaceIds.Contains(t.WorkspaceId) && t.CreatedAt >= start && t.CreatedAt < end)
            .Select(t => new { t.TaskId, WorkspaceId = t.WorkspaceId!, t.CreatedAt })
            .ToListAsync(ct);

        var taskIds = conversations.Select(c => c.TaskId).Concat(runs.Select(r => r.TaskId)).ToList();
        var spend = await db.Agents.AsNoTracking()
            .Where(a => taskIds.Contains(a.TaskId))
            .GroupBy(a => a.TaskId)
            .Select(g => new { TaskId = g.Key, Tokens = g.Sum(a => (long)a.TokensUsed), Cost = g.Sum(a => a.CostUsd) })
            .ToDictionaryAsync(x => x.TaskId, ct);

        var items = conversations.Select(c => (Task: c.TaskId, EndUser: (string?)c.EndUserId, Workspace: (string?)null, Day: c.CreatedAt.UtcDateTime.Date, Conversation: true))
            .Concat(runs.Select(r => (Task: r.TaskId, EndUser: (string?)null, Workspace: (string?)r.WorkspaceId, Day: r.CreatedAt.UtcDateTime.Date, Conversation: false)))
            .ToList();

        string? KeyFor((string Task, string? EndUser, string? Workspace, DateTime Day, bool Conversation) i) => by switch
        {
            "end_user" => i.EndUser,                                    // workspace runs aren't any one end user's
            "workspace" => i.Workspace is { } w ? keyOf.GetValueOrDefault(w, w) : "assistant",
            _ => i.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };

        var lines = items.Where(i => KeyFor(i) is not null).GroupBy(i => KeyFor(i)!)
            .Select(g => new UsageLine(g.Key, by == "workspace" && workspaces.ContainsKey(g.Key) ? g.Key : null,
                g.Count(i => i.Conversation), g.Count(i => !i.Conversation),
                g.Sum(i => spend.GetValueOrDefault(i.Task)?.Tokens ?? 0), g.Sum(i => spend.GetValueOrDefault(i.Task)?.Cost ?? 0)))
            .OrderByDescending(l => by == "day" ? 0 : l.CostUsd).ThenBy(l => l.Key)
            .ToList();

        return new Usage(start, end, by, spend.Values.Sum(s => s.Tokens), spend.Values.Sum(s => s.Cost), conversations.Count, runs.Count, lines);
    }

    /// <summary>What one end user's conversations have cost this calendar month.</summary>
    public async Task<decimal> EndUserMonthCostAsync(string tenantId, string installId, string endUserId, CancellationToken ct)
    {
        var start = MonthStart();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tasks = db.SolutionConversations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.InstallId == installId && c.EndUserId == endUserId && c.UpdatedAt >= start)
            .Select(c => c.TaskId);
        return await db.Agents.AsNoTracking().Where(a => tasks.Contains(a.TaskId) && a.CreatedAt >= start).SumAsync(a => (decimal?)a.CostUsd, ct) ?? 0;
    }

    public static string ToCsv(Usage usage)
    {
        var csv = new StringBuilder();
        csv.AppendLine($"{usage.By},conversations,runs,tokens,cost_usd");
        foreach (var l in usage.Lines)
        {
            var key = l.Key.Contains(',') || l.Key.Contains('"') ? $"\"{l.Key.Replace("\"", "\"\"")}\"" : l.Key;
            csv.AppendLine(string.Join(',', key, l.Conversations, l.Runs, l.Tokens, l.CostUsd.ToString("0.######", CultureInfo.InvariantCulture)));
        }

        return csv.ToString();
    }
}
