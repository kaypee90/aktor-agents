using AgentRuntime.Durability;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// The step journal in Postgres (table "JournalSteps"). One row per step, keyed by task, agent path,
/// kind and step key; recording a step again (re-run after a crash) replaces the row in place, so
/// its sequence number — its place in the run's order — stays the same.
/// </summary>
public sealed class PostgresStepJournal(IDbContextFactory<AgentDbContext> dbFactory) : IStepJournal
{
    public async Task RecordAsync(JournalStep step, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "JournalSteps" ("TenantId", "TaskId", "AgentId", "AgentPath", "Kind", "Key", "Step", "ToolName", "PayloadJson", "InputsReceived", "At")
            VALUES ({step.TenantId}, {step.TaskId}, {step.AgentId}, {step.AgentPath}, {step.Kind}, {step.Key}, {step.Step}, {step.ToolName}, {step.PayloadJson}, {step.InputsReceived}, {step.At})
            ON CONFLICT ("TaskId", "AgentPath", "Kind", "Key") DO UPDATE SET
                "AgentId" = EXCLUDED."AgentId", "Step" = EXCLUDED."Step", "ToolName" = EXCLUDED."ToolName",
                "PayloadJson" = EXCLUDED."PayloadJson", "InputsReceived" = EXCLUDED."InputsReceived", "At" = EXCLUDED."At"
            """, cancellationToken);
    }

    public async Task<JournalStep?> GetAsync(string taskId, string agentPath, string kind, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.JournalSteps.AsNoTracking()
            .FirstOrDefaultAsync(j => j.TaskId == taskId && j.AgentPath == agentPath && j.Kind == kind && j.Key == key, cancellationToken);
        return row is null ? null : ToStep(row);
    }

    public async Task<IReadOnlyList<JournalStep>> ListAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.JournalSteps.AsNoTracking().Where(j => j.TaskId == taskId).OrderBy(j => j.Id).ToListAsync(cancellationToken);
        return rows.Select(ToStep).ToList();
    }

    private static JournalStep ToStep(JournalStepRecord r) => new()
    {
        TenantId = r.TenantId,
        TaskId = r.TaskId,
        AgentId = r.AgentId,
        AgentPath = r.AgentPath,
        Kind = r.Kind,
        Key = r.Key,
        Step = r.Step,
        ToolName = r.ToolName,
        PayloadJson = r.PayloadJson,
        InputsReceived = r.InputsReceived,
        At = r.At,
        Seq = r.Id
    };
}
