using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.LLM;
using AgentRuntime.Solutions;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// The builder's conversation: the model's proposal is parsed into edits, and
/// <see cref="SolutionDraftEditor"/> places them in the draft (whole items, by key) without
/// touching anything else; the Mock provider answers it so it works with no API key.
/// </summary>
public sealed class SolutionDesignTests
{
    private static JsonObject Draft() => JsonNode.Parse("""
        {
          "format": 1, "name": "my-app", "title": "My app", "version": "0.1.0",
          "inputs": [{ "key": "company_name", "default": "Acme" }],
          "entities": { "Request": { "fields": { "subject": { "type": "string", "required": true } } } },
          "workspaces": [{ "key": "requests", "name": "Requests", "goal": "Handle requests.", "data": { "Request": "write" } }],
          "knowledge": [{ "key": "Policy", "text": "Be kind." }]
        }
        """)!.AsObject();

    private static SolutionEditOp Set(string section, string? key, string json) =>
        new() { Op = "set", Section = section, Key = key, Value = JsonNode.Parse(json)!.AsObject() };

    [Fact]
    public void Edits_put_whole_items_by_key_and_leave_the_rest_alone()
    {
        var before = Draft();
        var result = SolutionDraftEditor.Apply(before, [], [
            Set("basics", null, """{ "title": "Maintenance desk", "ignored": "x" }"""),
            Set("entity", "Vendor", """{ "fields": { "name": { "type": "string" }, "rate": { "type": "number" } } }"""),
            Set("workspace", "requests", """{ "name": "Requests", "goal": "Triage requests.", "data": { "Request": "write", "Vendor": "read" } }"""),
            Set("workspace", "renewals", """{ "name": "Renewals", "goal": "Renew leases." }"""),
            Set("skill", "triage", """{ "description": "How to triage", "instructions": "Urgent first." }"""),
            new SolutionEditOp { Op = "remove", Section = "knowledge", Key = "policy" },
            Set("seed", "Vendor", """{ "records": [{ "id": "v1", "name": "Ace Plumbing" }] }""")
        ]);

        Assert.Empty(result.Errors);
        var d = result.Draft;
        Assert.Equal("Maintenance desk", d["title"]!.GetValue<string>());
        Assert.Null(d["ignored"]);
        Assert.Equal("my-app", d["name"]!.GetValue<string>());
        Assert.NotNull(d["entities"]!["Request"]);
        Assert.Equal("number", d["entities"]!["Vendor"]!["fields"]!["rate"]!["type"]!.GetValue<string>());
        var workspaces = d["workspaces"]!.AsArray();
        Assert.Equal(2, workspaces.Count);
        Assert.Equal("Triage requests.", workspaces[0]!["goal"]!.GetValue<string>());
        Assert.Equal("requests", workspaces[0]!["key"]!.GetValue<string>()); // the key names the item
        Assert.Empty(d["knowledge"]!.AsArray());
        Assert.Single(d["seed"]!["Vendor"]!.AsArray());
        var skill = Assert.Single(result.Skills);
        Assert.Equal("triage", skill.Name);
        Assert.Equal("Urgent first.", skill.Instructions);
        Assert.Contains("Added entity Vendor", result.Changes);
        Assert.Contains("Updated workspace requests", result.Changes);

        // The draft it was given is untouched (the builder keeps it for undo).
        Assert.Equal("My app", before["title"]!.GetValue<string>());
        Assert.Single(before["workspaces"]!.AsArray());
    }

    [Fact]
    public void Edits_that_dont_fit_are_reported()
    {
        var result = SolutionDraftEditor.Apply(Draft(), [], [
            new SolutionEditOp { Op = "remove", Section = "entity", Key = "Nope" },
            new SolutionEditOp { Op = "set", Section = "widgets", Key = "x", Value = [] },
            new SolutionEditOp { Op = "set", Section = "workspace", Key = "x" },
            new SolutionEditOp { Op = "rename", Section = "entity", Key = "Request" }
        ]);

        Assert.Equal(4, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("no entity 'Nope'"));
        Assert.Contains(result.Errors, e => e.Contains("Unknown section 'widgets'"));
    }

    [Fact]
    public void A_proposal_is_parsed_and_a_question_alone_is_a_reply()
    {
        var call = new ToolCall
        {
            Id = "c1",
            Name = SolutionDesignPrompt.ToolName,
            ArgumentsJson = JsonSerializer.Serialize(new
            {
                reply = "Added vendors.",
                summary = "Adds a Vendor entity",
                changes = new object[] { new { op = "SET", section = "Entity", key = "Vendor", value = new { fields = new { } } } }
            })
        };
        var (proposal, error) = SolutionDesignPrompt.Parse(new LlmCompletionResponse { ToolCalls = [call] });
        Assert.Null(error);
        var op = Assert.Single(proposal!.Ops);
        Assert.Equal(("set", "entity", "Vendor"), (op.Op, op.Section, op.Key));

        var (question, _) = SolutionDesignPrompt.Parse(new LlmCompletionResponse { Content = "Who are your customers?" });
        Assert.Empty(question!.Ops);
        Assert.Equal("Who are your customers?", question.Reply);
    }

    [Fact]
    public void The_request_carries_the_draft_the_history_and_the_global_connectors()
    {
        var request = SolutionDesignPrompt.BuildRequest(Draft(), [new DraftSkill { Name = "triage", Description = "How to triage" }],
            [new DesignTurn("user", "Make a helpdesk"), new DesignTurn("assistant", "Done.")], "Add vendors",
            new SolutionDesignContext { GlobalConnectors = [("team_slack", "slack")] }, model: null, maxOutputTokens: 1000);

        Assert.StartsWith(SolutionDesignPrompt.SystemMarker, request.Messages[0].Content);
        Assert.Contains("team_slack = slack", request.Messages[0].Content);
        Assert.Equal(ChatRole.Assistant, request.Messages[2].Role);
        var last = request.Messages[^1].Content!;
        Assert.Contains("\"name\":\"my-app\"", last);
        Assert.Contains("What the person wants: Add vendors", last);
        Assert.Contains("- triage: How to triage", last);
        Assert.Equal(SolutionDesignPrompt.ToolName, Assert.Single(request.Tools).Name);
    }

    [Fact]
    public async Task The_mock_model_adds_an_entity_from_a_plain_request()
    {
        var request = SolutionDesignPrompt.BuildRequest(Draft(), [], [], "Add a Vendor entity with name, phone and hourly rate, and let the workspace assign vendors to requests.",
            new SolutionDesignContext(), model: null, maxOutputTokens: 1000);
        var response = await new HeuristicMockLlmProvider().CompleteAsync(request);

        var (proposal, error) = SolutionDesignPrompt.Parse(response);
        Assert.Null(error);
        var result = SolutionDraftEditor.Apply(Draft(), [], proposal!.Ops);
        Assert.Empty(result.Errors);
        var fields = result.Draft["entities"]!["Vendor"]!["fields"]!.AsObject();
        Assert.Equal(["name", "phone", "hourly_rate"], fields.Select(f => f.Key));
        Assert.Equal("number", fields["hourly_rate"]!["type"]!.GetValue<string>());
    }
}
