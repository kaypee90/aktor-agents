using AgentRuntime.Durability;
using AgentRuntime.LLM;
using AgentRuntime.Tools;

namespace AgentRuntime.Tests;

/// <summary>Roadmap P6: comparing two runs step by step despite their different ids.</summary>
public sealed class RunDiffTests
{
    private static JournalStep Llm(string task, string agent, string path, int step, long seq, params ToolCall[] calls) => new()
    {
        TaskId = task, AgentId = agent, AgentPath = path, Kind = JournalStep.LlmKind, Key = step.ToString(), Step = step, Seq = seq,
        PayloadJson = ReplayPolicy.LlmPayload(new LlmCompletionResponse { ToolCalls = calls, InputTokens = (int)seq * 100 })
    };

    private static JournalStep Tool(string task, string agent, string path, string callId, string name, long seq, string resultJson) => new()
    {
        TaskId = task, AgentId = agent, AgentPath = path, Kind = JournalStep.ToolKind, Key = callId, ToolName = name, Seq = seq,
        PayloadJson = ReplayPolicy.ToolPayload(ToolExecutionResult.Ok(resultJson))
    };

    private static ToolCall Spawn(string id) => new() { Id = id, Name = "spawn_agent", ArgumentsJson = """{"role":"Analyst"}""" };

    [Fact]
    public void Runs_with_different_ids_but_the_same_behavior_are_identical()
    {
        IReadOnlyList<JournalStep> a =
        [
            Llm("A", "root-aaaaaaaa", "r", 1, 1, Spawn("call_1")),
            Tool("A", "root-aaaaaaaa", "r", "call_1", "spawn_agent", 2, """{"agent_id":"agent-1111aaaa","granted_budget":{"max_duration_seconds":880}}"""),
            Llm("A", "agent-1111aaaa", "r/call_1", 1, 3)
        ];
        IReadOnlyList<JournalStep> b =
        [
            Llm("B", "root-bbbbbbbb", "r", 1, 10, Spawn("call_1")),
            Tool("B", "root-bbbbbbbb", "r", "call_1", "spawn_agent", 11, """{"agent_id":"agent-2222bbbb","granted_budget":{"max_duration_seconds":871}}"""),
            Llm("B", "agent-2222bbbb", "r/call_1", 1, 12)
        ];

        var diff = RunDiff.Compare(a, b);
        Assert.True(diff.Identical, string.Join("; ", diff.Steps.Select(s => $"{s.Status} {s.AgentPath} {s.Key}")));
        Assert.Equal(3, diff.Same);
    }

    [Fact]
    public void Different_decisions_and_missing_agents_are_reported()
    {
        IReadOnlyList<JournalStep> a =
        [
            Llm("A", "root-aaaaaaaa", "r", 1, 1, Spawn("call_1")),
            Llm("A", "agent-1111aaaa", "r/call_1", 1, 2)
        ];
        IReadOnlyList<JournalStep> b =
        [
            Llm("B", "root-bbbbbbbb", "r", 1, 1, new ToolCall { Id = "call_9", Name = "complete_task", ArgumentsJson = "{}" })
        ];

        var diff = RunDiff.Compare(a, b);
        Assert.False(diff.Identical);
        Assert.Equal(StepDiffStatus.Different, diff.Steps.Single(s => s.AgentPath == "r").Status);
        Assert.Equal(StepDiffStatus.OnlyInA, diff.Steps.Single(s => s.AgentPath == "r/call_1").Status);
        Assert.Equal(["r/call_1"], diff.AgentsOnlyInA);
        Assert.Contains("spawn_agent", diff.Steps.Single(s => s.AgentPath == "r").SummaryA);
        Assert.Contains("complete_task", diff.Steps.Single(s => s.AgentPath == "r").SummaryB);
    }

    [Fact]
    public void Payloads_round_trip_through_the_journal()
    {
        var response = new LlmCompletionResponse
        {
            Content = "thinking aloud",
            ToolCalls = [new ToolCall { Id = "c", Name = "x", ArgumentsJson = """{"a":1}""", ProviderSignature = "sig" }],
            FinishReason = LlmFinishReason.ToolCalls,
            InputTokens = 10, OutputTokens = 2, CachedInputTokens = 5
        };
        var back = ReplayPolicy.LlmResponse(ReplayPolicy.LlmPayload(response));
        Assert.Equal(response.Content, back.Content);
        Assert.Equal("sig", back.ToolCalls[0].ProviderSignature);
        Assert.Equal(5, back.CachedInputTokens);

        var failed = ToolExecutionResult.Fail("nope", "spawn_rejected.duplicate_role", """{"rule":"duplicate_role"}""");
        var restored = ReplayPolicy.ToolResult(ReplayPolicy.ToolPayload(failed));
        Assert.False(restored.Success);
        Assert.Equal("spawn_rejected.duplicate_role", restored.ErrorCode);
    }

    [Theory]
    [InlineData("spawn_agent", true)]
    [InlineData("filesystem_write", true)]
    [InlineData("web_search", false)]
    [InlineData("shell_exec", false)]
    [InlineData("crm__create_ticket", false)]
    [InlineData("notify_user", false)]
    [InlineData("write_memory", false)]
    public void Replays_rerun_only_internal_actions(string tool, bool reruns) => Assert.Equal(reruns, ReplayPolicy.ReRuns(tool));
}
