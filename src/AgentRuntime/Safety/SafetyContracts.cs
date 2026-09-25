using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;

namespace AgentRuntime.Safety;

public enum PolicyDecisionKind
{
    Allow,
    Deny,
    RequireApproval
}

/// <summary>Which calls a rule applies to, by what they can do.</summary>
public enum SideEffectScope
{
    /// <summary>Every call, reads included.</summary>
    Any,
    /// <summary>Calls that change something (Idempotent or NonIdempotent).</summary>
    Writes,
    /// <summary>Calls that aren't safe to repeat (NonIdempotent): sends, payments, deletes, shell.</summary>
    Unsafe
}

/// <summary>An explicit policy rule. Rules are checked in order; the first match decides.</summary>
[GenerateSerializer]
public sealed record ApprovalRule
{
    [Id(0)] public string Id { get; init; } = Guid.NewGuid().ToString("n")[..8];
    [Id(1)] public string Name { get; init; } = string.Empty;
    /// <summary>Tool-name glob: * matches anything, e.g. "billing__*", "*__send_sms", "shell_exec".</summary>
    [Id(2)] public string ToolPattern { get; init; } = "*";
    [Id(3)] public SideEffectScope Applies { get; init; } = SideEffectScope.Writes;
    [Id(4)] public PolicyDecisionKind Decision { get; init; } = PolicyDecisionKind.RequireApproval;
}

[GenerateSerializer]
public sealed record WorkspaceSafetyPolicy
{
    [Id(0)] public AutonomyLevel Autonomy { get; init; } = AutonomyLevel.Autonomous;
    [Id(1)] public List<ApprovalRule> Rules { get; init; } = [];
    /// <summary>Unanswered approval requests are rejected after this long.</summary>
    [Id(2)] public int ApprovalTimeoutHours { get; init; } = 72;
}

public sealed record PolicyVerdict(PolicyDecisionKind Decision, string Reason);

/// <summary>
/// Decides whether a tool call may run, needs a human's approval, or is blocked. Pure and
/// deterministic: the runtime calls it for every tool call of every workspace agent, and the LLM
/// has no way to influence it (CLAUDE.md sections 18, 32, 44: no LLM-controlled security policy).
/// </summary>
public static class PolicyEngine
{
    /// <summary>Tools with effects outside the platform. Everything else (agents messaging each
    /// other, schedules, memory, notifying the user, the sandboxed workspace files, simulations)
    /// is internal and never needs a human's approval.</summary>
    public static bool IsExternal(string toolName) =>
        toolName.Contains("__", StringComparison.Ordinal) || toolName is "shell_exec" or "http_request" or "database_query";

    public static PolicyVerdict Evaluate(WorkspaceSafetyPolicy policy, string toolName, ToolSideEffects sideEffects)
    {
        foreach (var rule in policy.Rules)
        {
            if (!Covers(rule.Applies, sideEffects) || !Glob(rule.ToolPattern, toolName)) continue;
            var label = string.IsNullOrWhiteSpace(rule.Name) ? rule.ToolPattern : rule.Name;
            return new PolicyVerdict(rule.Decision, $"rule '{label}'");
        }

        // Reads never wait for a human; internal actions are the platform's own business.
        if (sideEffects == ToolSideEffects.ReadOnly || !IsExternal(toolName))
        {
            return new PolicyVerdict(PolicyDecisionKind.Allow, "no rule applies");
        }

        return policy.Autonomy switch
        {
            AutonomyLevel.Supervised => new PolicyVerdict(PolicyDecisionKind.RequireApproval, "supervised mode: external writes need approval"),
            AutonomyLevel.SemiAutonomous when sideEffects == ToolSideEffects.NonIdempotent =>
                new PolicyVerdict(PolicyDecisionKind.RequireApproval, "semi-autonomous mode: actions that can't be undone or repeated safely need approval"),
            _ => new PolicyVerdict(PolicyDecisionKind.Allow, "autonomous mode")
        };
    }

    private static bool Covers(SideEffectScope scope, ToolSideEffects effects) => scope switch
    {
        SideEffectScope.Any => true,
        SideEffectScope.Writes => effects != ToolSideEffects.ReadOnly,
        SideEffectScope.Unsafe => effects == ToolSideEffects.NonIdempotent,
        _ => false
    };

    public static bool Glob(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
}

// ---- Approvals ----------------------------------------------------------------

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Expired
}

[GenerateSerializer]
public sealed class ApprovalRecord
{
    [Id(0)] public required string ApprovalId { get; set; }
    /// <summary>Short code the user can reply with over SMS/chat: "A7".</summary>
    [Id(1)] public required string Code { get; set; }
    /// <summary>The tool call's idempotency key: the same call always maps to the same approval.</summary>
    [Id(2)] public required string CallKey { get; set; }
    [Id(3)] public required string AgentId { get; set; }
    [Id(4)] public string AgentName { get; set; } = string.Empty;
    [Id(5)] public required string ToolName { get; set; }
    [Id(6)] public ToolSideEffects SideEffects { get; set; }
    [Id(7)] public string ArgumentsJson { get; set; } = "{}";
    /// <summary>What the agent said alongside the call (its visible reply, not hidden reasoning).</summary>
    [Id(8)] public string? AgentNote { get; set; }
    [Id(9)] public string PolicyReason { get; set; } = string.Empty;
    [Id(10)] public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    [Id(11)] public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    [Id(12)] public DateTimeOffset ExpiresAt { get; set; }
    [Id(13)] public DateTimeOffset? DecidedAt { get; set; }
    [Id(14)] public string? DecidedBy { get; set; }
    [Id(15)] public string? DecisionReason { get; set; }
}

/// <summary>What the runtime tells an agent about one tool call.</summary>
[GenerateSerializer]
public sealed record ToolCallPermission
{
    [Id(0)] public PolicyDecisionKind Decision { get; init; }
    /// <summary>For RequireApproval: whether a human has decided yet.</summary>
    [Id(1)] public ApprovalStatus? ApprovalStatus { get; init; }
    [Id(2)] public string? ApprovalCode { get; init; }
    [Id(3)] public string Message { get; init; } = string.Empty;

    public bool MayRun => Decision == PolicyDecisionKind.Allow || ApprovalStatus == Safety.ApprovalStatus.Approved;
    public bool IsWaiting => Decision == PolicyDecisionKind.RequireApproval && ApprovalStatus == Safety.ApprovalStatus.Pending;
}

[GenerateSerializer]
public sealed record ToolCallPermissionRequest
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string ToolName { get; init; }
    [Id(2)] public ToolSideEffects SideEffects { get; init; }
    [Id(3)] public required string CallKey { get; init; }
    [Id(4)] public string ArgumentsJson { get; init; } = "{}";
    [Id(5)] public string? AgentNote { get; init; }
}

// ---- Audit log ------------------------------------------------------------------

/// <summary>One audited action. Records are append-only and hash-chained per scope.</summary>
public sealed record AuditEntry
{
    /// <summary>The workspace (or task) the action belongs to.</summary>
    public required string Scope { get; init; }
    /// <summary>Deduplication key: an action replayed after a crash is recorded once.</summary>
    public required string Key { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>"agent", "user", "system" or "watch".</summary>
    public required string ActorType { get; init; }
    public required string ActorId { get; init; }
    public string ActorName { get; init; } = string.Empty;
    /// <summary>e.g. tool.call, tool.denied, approval.requested, approval.approved, connection.added.</summary>
    public required string Action { get; init; }
    public string Target { get; init; } = string.Empty;
    public string? SideEffects { get; init; }
    /// <summary>"ok", "failed", "denied", "pending", "unknown".</summary>
    public string Outcome { get; init; } = "ok";
    public string Summary { get; init; } = string.Empty;
    public string DetailJson { get; init; } = "{}";

    // Set by the log when appended.
    public long Seq { get; init; }
    public string PreviousHash { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
}

public sealed record AuditQuery
{
    public required string Scope { get; init; }
    public string? ActorId { get; init; }
    public string? ActionPrefix { get; init; }
    public string? Text { get; init; }
    public DateTimeOffset? Since { get; init; }
    public long? BeforeSeq { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record AuditVerification(bool Valid, long Records, long? FirstBrokenSeq, string Message);

/// <summary>Append-only, tamper-evident record of what agents and people did.</summary>
public interface IAuditLog
{
    /// <summary>Appends unless an entry with the same key exists (idempotent).</summary>
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default);
    /// <summary>Recomputes the hash chain; any edited, inserted or deleted record breaks it.</summary>
    Task<AuditVerification> VerifyAsync(string scope, CancellationToken cancellationToken = default);
}

public static class AuditHasher
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>SHA-256 over the previous hash and every field of the record (not its own hash),
    /// each length-prefixed so no two different records serialize the same.</summary>
    /// <summary>Timestamps are hashed at microsecond precision, which is what Postgres stores.</summary>
    public static DateTimeOffset Normalize(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    public static string Compute(string previousHash, AuditEntry e)
    {
        var sb = new StringBuilder();
        void Add(string? v) => sb.Append((v ?? string.Empty).Length).Append(':').Append(v).Append('|');
        Add(previousHash);
        Add(e.Scope);
        Add(e.Seq.ToString());
        Add(e.Key);
        Add(e.At.UtcDateTime.ToString("O"));
        Add(e.ActorType);
        Add(e.ActorId);
        Add(e.ActorName);
        Add(e.Action);
        Add(e.Target);
        Add(e.SideEffects);
        Add(e.Outcome);
        Add(e.Summary);
        Add(e.DetailJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public static AuditVerification Verify(IReadOnlyList<AuditEntry> ordered)
    {
        var previous = Genesis;
        long expectedSeq = 1;
        foreach (var e in ordered)
        {
            if (e.Seq != expectedSeq)
            {
                return new AuditVerification(false, ordered.Count, expectedSeq, $"Record {expectedSeq} is missing.");
            }

            if (e.PreviousHash != previous || Compute(previous, e) != e.Hash)
            {
                return new AuditVerification(false, ordered.Count, e.Seq, $"Record {e.Seq} doesn't match its hash: it was changed, or the chain was spliced.");
            }

            previous = e.Hash;
            expectedSeq++;
        }

        return new AuditVerification(true, ordered.Count, null, $"All {ordered.Count} records verified.");
    }
}

/// <summary>In-process audit log (tests and Memory mode). Same chaining as the durable one.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly Dictionary<string, List<AuditEntry>> _byScope = [];
    private readonly HashSet<string> _keys = [];
    private readonly Lock _lock = new();

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_keys.Add(entry.Key)) return Task.CompletedTask;
            if (!_byScope.TryGetValue(entry.Scope, out var list)) _byScope[entry.Scope] = list = [];
            var previous = list.Count == 0 ? AuditHasher.Genesis : list[^1].Hash;
            var sequenced = entry with { At = AuditHasher.Normalize(entry.At), Seq = list.Count + 1, PreviousHash = previous };
            list.Add(sequenced with { Hash = AuditHasher.Compute(previous, sequenced) });
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery q, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IEnumerable<AuditEntry> list = _byScope.GetValueOrDefault(q.Scope) ?? [];
            if (q.ActorId is { } a) list = list.Where(e => e.ActorId == a);
            if (q.ActionPrefix is { } p) list = list.Where(e => e.Action.StartsWith(p, StringComparison.Ordinal));
            if (q.Text is { } t) list = list.Where(e => e.Summary.Contains(t, StringComparison.OrdinalIgnoreCase) || e.Target.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (q.Since is { } since) list = list.Where(e => e.At >= since);
            if (q.BeforeSeq is { } before) list = list.Where(e => e.Seq < before);
            return Task.FromResult<IReadOnlyList<AuditEntry>>(list.OrderByDescending(e => e.Seq).Take(q.Limit).ToList());
        }
    }

    public Task<AuditVerification> VerifyAsync(string scope, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(AuditHasher.Verify(_byScope.GetValueOrDefault(scope)?.ToList() ?? []));
        }
    }

    /// <summary>Test hook: simulate someone editing a stored record.</summary>
    public void TamperForTest(string scope, long seq, Func<AuditEntry, AuditEntry> edit)
    {
        lock (_lock)
        {
            var list = _byScope[scope];
            var i = list.FindIndex(e => e.Seq == seq);
            list[i] = edit(list[i]);
        }
    }
}
