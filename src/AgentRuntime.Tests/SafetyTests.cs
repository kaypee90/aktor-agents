using AgentRuntime.Contracts;
using AgentRuntime.Safety;
using AgentRuntime.Tools;
using Xunit;

namespace AgentRuntime.Tests;

public sealed class PolicyEngineTests
{
    private static PolicyDecisionKind Decide(WorkspaceSafetyPolicy p, string tool, ToolSideEffects fx) => PolicyEngine.Evaluate(p, tool, fx).Decision;

    [Theory]
    [InlineData(AutonomyLevel.Autonomous, ToolSideEffects.NonIdempotent, PolicyDecisionKind.Allow)]
    [InlineData(AutonomyLevel.SemiAutonomous, ToolSideEffects.NonIdempotent, PolicyDecisionKind.RequireApproval)]
    [InlineData(AutonomyLevel.SemiAutonomous, ToolSideEffects.Idempotent, PolicyDecisionKind.Allow)]
    [InlineData(AutonomyLevel.Supervised, ToolSideEffects.Idempotent, PolicyDecisionKind.RequireApproval)]
    [InlineData(AutonomyLevel.Supervised, ToolSideEffects.ReadOnly, PolicyDecisionKind.Allow)]
    public void AutonomyLevels_GateExternalWrites(AutonomyLevel level, ToolSideEffects fx, PolicyDecisionKind expected) =>
        Assert.Equal(expected, Decide(new() { Autonomy = level }, "billing__refund", fx));

    [Theory]
    [InlineData("spawn_agent")]
    [InlineData("send_message")]
    [InlineData("complete_task")]
    [InlineData("filesystem_write")]
    public void InternalTools_NeverNeedApproval_EvenWhenSupervised(string tool) =>
        Assert.Equal(PolicyDecisionKind.Allow, Decide(new() { Autonomy = AutonomyLevel.Supervised }, tool, ToolSideEffects.NonIdempotent));

    [Theory]
    [InlineData("shell_exec")]
    [InlineData("http_request")]
    [InlineData("database_query")]
    [InlineData("crm__delete_customer")]
    public void BuiltInsWithOutsideEffects_AreExternal(string tool) => Assert.True(PolicyEngine.IsExternal(tool));

    [Fact]
    public void Rules_OverrideTheAutonomyLevel_FirstMatchWins()
    {
        var policy = new WorkspaceSafetyPolicy
        {
            Autonomy = AutonomyLevel.Autonomous,
            Rules =
            [
                new() { ToolPattern = "billing__*", Applies = SideEffectScope.Writes, Decision = PolicyDecisionKind.RequireApproval },
                new() { ToolPattern = "*", Applies = SideEffectScope.Unsafe, Decision = PolicyDecisionKind.Deny },
                new() { ToolPattern = "*__send_sms", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Allow }
            ]
        };

        Assert.Equal(PolicyDecisionKind.RequireApproval, Decide(policy, "billing__refund", ToolSideEffects.NonIdempotent)); // rule 1 before rule 2
        Assert.Equal(PolicyDecisionKind.Allow, Decide(policy, "billing__get", ToolSideEffects.ReadOnly));                  // Writes scope skips reads
        Assert.Equal(PolicyDecisionKind.Deny, Decide(policy, "sms__send_sms", ToolSideEffects.NonIdempotent));             // rule 2 before rule 3
        Assert.Equal(PolicyDecisionKind.Allow, Decide(policy, "crm__update", ToolSideEffects.Idempotent));
    }

    [Theory]
    [InlineData("billing__*", "Billing__Refund", true)]
    [InlineData("*__send_*", "sms__send_sms", true)]
    [InlineData("shell_exec", "shell_exec_2", false)]
    [InlineData("a.b", "axb", false)] // no regex injection
    public void Glob_MatchesWholeNames_CaseInsensitively(string pattern, string name, bool expected) =>
        Assert.Equal(expected, PolicyEngine.Glob(pattern, name));

    // ---- The organization's policy, on top of a workspace's ----

    private static PolicyVerdict Both(OrganizationSafetyPolicy org, WorkspaceSafetyPolicy? ws, string tool, ToolSideEffects fx) =>
        PolicyEngine.Evaluate(org, ws, tool, fx);

    [Fact]
    public void An_organization_rule_wins_over_a_looser_workspace()
    {
        var org = new OrganizationSafetyPolicy { Rules = [new ApprovalRule { Name = "no deletes", ToolPattern = "*__delete_*", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Deny }] };
        var allowAll = new WorkspaceSafetyPolicy { Rules = [new ApprovalRule { ToolPattern = "*", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Allow }] };
        var verdict = Both(org, allowAll, "crm__delete_customer", ToolSideEffects.NonIdempotent);
        Assert.Equal(PolicyDecisionKind.Deny, verdict.Decision);
        Assert.True(verdict.ByOrganization);
        Assert.Contains("organization", verdict.Reason);
        Assert.Equal(PolicyDecisionKind.Allow, Both(org, allowAll, "crm__update_customer", ToolSideEffects.Idempotent).Decision);
    }

    [Fact]
    public void The_organizations_minimum_autonomy_applies_to_an_autonomous_workspace_and_to_tasks_but_reads_stay_free()
    {
        var org = new OrganizationSafetyPolicy { MinimumAutonomy = AutonomyLevel.Supervised };
        Assert.Equal(PolicyDecisionKind.RequireApproval, Both(org, new WorkspaceSafetyPolicy(), "crm__update_customer", ToolSideEffects.Idempotent).Decision);
        Assert.Equal(PolicyDecisionKind.RequireApproval, Both(org, null, "crm__update_customer", ToolSideEffects.Idempotent).Decision);
        Assert.Equal(PolicyDecisionKind.Allow, Both(org, new WorkspaceSafetyPolicy(), "crm__lookup_customer", ToolSideEffects.ReadOnly).Decision);
        Assert.Equal(PolicyDecisionKind.Allow, Both(org, null, "write_memory", ToolSideEffects.Idempotent).Decision); // internal
    }

    [Fact]
    public void An_organization_allow_only_exempts_from_its_own_minimum_and_a_stricter_workspace_still_wins()
    {
        var org = new OrganizationSafetyPolicy
        {
            MinimumAutonomy = AutonomyLevel.Supervised,
            Rules = [new ApprovalRule { ToolPattern = "notes__*", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Allow }]
        };
        Assert.Equal(PolicyDecisionKind.Allow, Both(org, new WorkspaceSafetyPolicy(), "notes__save", ToolSideEffects.Idempotent).Decision);

        var strict = new WorkspaceSafetyPolicy { Rules = [new ApprovalRule { ToolPattern = "notes__*", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Deny }] };
        var verdict = Both(org, strict, "notes__save", ToolSideEffects.Idempotent);
        Assert.Equal(PolicyDecisionKind.Deny, verdict.Decision);
        Assert.False(verdict.ByOrganization);
    }

    [Fact]
    public void An_empty_organization_policy_changes_nothing()
    {
        var ws = new WorkspaceSafetyPolicy { Autonomy = AutonomyLevel.SemiAutonomous };
        Assert.Equal(PolicyEngine.Evaluate(ws, "crm__delete_customer", ToolSideEffects.NonIdempotent),
            Both(new OrganizationSafetyPolicy(), ws, "crm__delete_customer", ToolSideEffects.NonIdempotent));
        Assert.Equal(PolicyDecisionKind.Allow, Both(new OrganizationSafetyPolicy(), null, "shell_exec", ToolSideEffects.NonIdempotent).Decision);
    }
}

public sealed class AuditLogTests
{
    private static AuditEntry Entry(string key, string summary = "did something") => new()
    {
        Scope = "ws-1", Key = key, ActorType = "agent", ActorId = "agent-1", Action = "tool.call", Target = "crm__get", Summary = summary,
        At = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567) // sub-microsecond ticks are dropped
    };

    [Fact]
    public async Task Appends_AreChained_Sequenced_AndIdempotentByKey()
    {
        var log = new InMemoryAuditLog();
        await log.AppendAsync(Entry("k1"));
        await log.AppendAsync(Entry("k2"));
        await log.AppendAsync(Entry("k1")); // a replay after a crash

        var entries = (await log.QueryAsync(new AuditQuery { Scope = "ws-1" })).OrderBy(e => e.Seq).ToList();
        Assert.Equal([1L, 2L], entries.Select(e => e.Seq));
        Assert.Equal(AuditHasher.Genesis, entries[0].PreviousHash);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.Equal(0, entries[0].At.Ticks % 10);
        Assert.True((await log.VerifyAsync("ws-1")).Valid);
    }

    [Fact]
    public async Task EditingARecord_BreaksTheChain_AtThatRecord()
    {
        var log = new InMemoryAuditLog();
        for (var i = 1; i <= 3; i++) await log.AppendAsync(Entry($"k{i}"));

        log.TamperForTest("ws-1", 2, e => e with { Summary = "nothing to see here" });
        var result = await log.VerifyAsync("ws-1");

        Assert.False(result.Valid);
        Assert.Equal(2, result.FirstBrokenSeq);
    }

    [Fact]
    public void RecomputingTheHash_AfterAnEdit_StillBreaksTheNextLink()
    {
        var a = Entry("k1") with { Seq = 1, PreviousHash = AuditHasher.Genesis };
        a = a with { Hash = AuditHasher.Compute(AuditHasher.Genesis, a) };
        var b = Entry("k2") with { Seq = 2, PreviousHash = a.Hash };
        b = b with { Hash = AuditHasher.Compute(a.Hash, b) };

        var forged = a with { Summary = "forged" };
        forged = forged with { Hash = AuditHasher.Compute(AuditHasher.Genesis, forged) };

        Assert.True(AuditHasher.Verify([a, b]).Valid);
        Assert.Equal(2, AuditHasher.Verify([forged, b]).FirstBrokenSeq);
        Assert.Equal(1, AuditHasher.Verify([b]).FirstBrokenSeq); // a deleted first record
    }

    [Fact]
    public void FieldBoundaries_AreUnambiguous()
    {
        var x = Entry("k") with { ActorName = "ab", Action = "c" };
        var y = Entry("k") with { ActorName = "a", Action = "bc" };
        Assert.NotEqual(AuditHasher.Compute(AuditHasher.Genesis, x), AuditHasher.Compute(AuditHasher.Genesis, y));
    }
}
