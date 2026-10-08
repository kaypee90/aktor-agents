using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure;
using AgentRuntime.LLM;
using AgentRuntime.Memory;
using AgentRuntime.Pipelines;
using AgentRuntime.Tools;

namespace AgentRuntime.Tests;

/// <summary>Mentioning knowledge (<c>@knowledge:refund-policy.docx</c>, docs/workspaces.md#mentions):
/// one handle per file or fact, an agent told to read what it was pointed to, and read_knowledge
/// returning a whole document in order, from the agent's own scope only.</summary>
public sealed class KnowledgeMentionTests
{
    [Theory]
    [InlineData("Refund Policy 2024.docx", "refund-policy-2024.docx")]
    [InlineData("  On-call rota  ", "on-call-rota")]
    [InlineData("pricing_v2.md", "pricing_v2.md")]
    [InlineData("Q&A (final).pdf", "q-a-final-.pdf")]
    public void Knowledge_gets_one_handle(string name, string handle) => Assert.Equal(handle, KnowledgeFiles.Handle(name));

    [Fact]
    public void Mentions_of_knowledge_are_found_and_explained()
    {
        Assert.Equal(["refund-policy.docx", "on-call-rota"],
            Mentions.KnowledgeIn(["Use @knowledge:refund-policy.docx and @knowledge:on-call-rota.", "Ask @skill:support, mail me@x.com."]));
        var lines = Mentions.Describe(["Check @knowledge:refund-policy.docx"], [], []);
        Assert.Contains(lines, l => l.StartsWith("@knowledge:refund-policy.docx:") && l.Contains("read_knowledge"));
    }

    [Fact]
    public void An_agent_pointed_to_knowledge_is_told_to_read_it_first()
    {
        var section = new KnowledgeMentionsSection();
        var state = new AgentState { AgentId = "a1", Name = "A", Role = "worker", Goal = "Answer the ticket using @knowledge:refund-policy.docx." };
        var context = new AgentPromptContext { State = state, AvailableTools = [], AutonomyLevel = AutonomyLevel.Autonomous, EnvironmentSummary = "env" };
        Assert.Contains("@knowledge:refund-policy.docx", section.Render(context));
        Assert.Contains("read_knowledge before anything else", section.Render(context));

        state.Goal = "No knowledge named.";
        Assert.Empty(section.Render(context));
    }

    private static async Task Add(IMemoryStore memory, string key, string value, string? workspaceId = null) =>
        await memory.WriteAsync(new MemoryRecord { TenantId = "t1", AgentId = "user", Kind = MemoryKind.Shared, Key = key, Value = value, WorkspaceId = workspaceId });

    private static ToolExecutionRequest Request(string name, string? workspaceId = null, string tenant = "t1") => new()
    {
        ToolName = "read_knowledge", AgentId = "agent-1", TaskId = "task-1", TenantId = tenant, WorkspaceId = workspaceId,
        ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { name })
    };

    [Fact]
    public async Task Read_knowledge_returns_a_whole_document_in_order_from_the_agents_own_scope()
    {
        var memory = new InMemoryMemoryStore();
        await Add(memory, "Refund Policy.docx (part 2 of 2)", "Part two: annual plans within 30 days.");
        await Add(memory, "Refund Policy.docx (part 1 of 2)", "Part one: monthly plans aren't refunded.");
        await Add(memory, "On-call rota", "Ama is on call.", workspaceId: "ws-a");
        var tool = new ReadKnowledgeTool(memory);

        var doc = await tool.ExecuteAsync(Request("@knowledge:refund-policy.docx"));
        Assert.True(doc.Success, doc.ErrorMessage);
        Assert.Contains("\"passages\":2", doc.ResultJson);
        Assert.True(doc.ResultJson.IndexOf("Part one", StringComparison.Ordinal) < doc.ResultJson.IndexOf("Part two", StringComparison.Ordinal));

        // A workspace's own knowledge: found inside that workspace, not outside it or from another organization.
        Assert.True((await tool.ExecuteAsync(Request("on-call-rota", "ws-a"))).Success);
        var outside = await tool.ExecuteAsync(Request("on-call-rota"));
        Assert.False(outside.Success);
        Assert.Contains("Refund Policy.docx", outside.ErrorMessage);
        Assert.False((await tool.ExecuteAsync(Request("on-call-rota", "ws-b"))).Success);
        Assert.False((await tool.ExecuteAsync(Request("refund-policy.docx", tenant: "t2"))).Success);
    }
}
