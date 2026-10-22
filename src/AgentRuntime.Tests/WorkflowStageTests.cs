using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Pipelines;
using AgentRuntime.Pipelines;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Tests;

/// <summary>The pieces of action, script and form stages that don't need a cluster.</summary>
public sealed class WorkflowStageTests
{
    private static JsonObject Data() => (JsonObject)JsonNode.Parse("""
        {
          "input": { "email": "ama@example.com", "seats": 12, "tags": ["a", "b"] },
          "stages": { "read-sheet": { "rows": [ { "Name": "Ama", "Full name": "Ama Mensah" }, { "Name": "Kofi" } ] } },
          "previous": { "count": 2 },
          "run": { "number": 7 }
        }
        """)!;

    // ---- Expressions ----

    [Fact]
    public void A_value_that_is_only_an_expression_keeps_its_type()
    {
        var args = PipelineExpressions.Render("""{ "seats": "{{ input.seats }}", "tags": "{{input.tags}}", "rows": "{{ stages.read-sheet.rows }}" }""", Data());

        Assert.Equal(12, args["seats"]!.GetValue<int>());
        Assert.Equal(2, args["tags"]!.AsArray().Count);
        Assert.Equal("Ama", args["rows"]![0]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Expressions_inside_text_are_written_in_and_missing_values_are_empty()
    {
        var args = PipelineExpressions.Render("""{ "subject": "Run {{ run.number }} for {{ input.email }}{{ input.nothing }}", "nested": [ { "n": "{{ previous.count }}" } ] }""", Data());

        Assert.Equal("Run 7 for ama@example.com", args["subject"]!.GetValue<string>());
        Assert.Equal(2, args["nested"]![0]!["n"]!.GetValue<int>());
    }

    [Fact]
    public void Wildcards_and_quoted_names_select_from_lists()
    {
        Assert.Equal("""["Ama","Kofi"]""", PipelineExpressions.Evaluate("stages.read-sheet.rows[*].Name", Data())!.ToJsonString());
        Assert.Equal("Ama Mensah", PipelineExpressions.Evaluate("stages['read-sheet'].rows[0]['Full name']", Data())!.GetValue<string>());
        Assert.Null(PipelineExpressions.Evaluate("input.missing.deeper", Data()));
    }

    [Theory]
    [InlineData("not json", "valid JSON")]
    [InlineData("[1, 2]", "JSON object")]
    [InlineData("""{ "a": "{{ secrets.key }}" }""", "doesn't say where the value comes from")]
    [InlineData("""{ "path": "/users/{{ user_id }}" }""", "Write {{ input.user_id }}")]
    [InlineData("""{ "a": "{{ input.rows[ }}" }""", "isn't a valid path")]
    public void Bad_templates_are_refused_with_the_reason(string template, string expected)
    {
        Assert.Contains(expected, PipelineExpressions.CheckTemplate(template));
    }

    [Fact]
    public void Good_templates_pass()
    {
        Assert.Null(PipelineExpressions.CheckTemplate("""{ "rows": "{{ previous.rows }}", "n": 3, "list": ["{{ item.id }}"] }"""));
        Assert.Null(PipelineExpressions.CheckTemplate(""));
    }

    // ---- Validation ----

    private static List<string> Validate(params PipelineStage[] stages) =>
        PipelineValidator.Validate(new PipelineDefinition { Stages = [.. stages] }, new PipelineOptions());

    [Fact]
    public void Action_and_script_stages_need_their_own_settings_but_no_instructions()
    {
        Assert.Empty(Validate(new PipelineStage { StageId = "a", Name = "A", Kind = StageKind.Action, Tool = "sheets__read_rows", Arguments = """{ "range": "Leads" }""" }));
        Assert.Empty(Validate(new PipelineStage { StageId = "s", Name = "S", Kind = StageKind.Script, Language = "javascript", Code = "return data.input;" }));

        Assert.Contains(Validate(new PipelineStage { StageId = "a", Name = "A", Kind = StageKind.Action, Tool = "read_rows" }), e => e.Contains("connection tool"));
        Assert.Contains(Validate(new PipelineStage { StageId = "a", Name = "A", Kind = StageKind.Action, Tool = "x__y", Arguments = "{" }), e => e.Contains("valid JSON"));
        Assert.Contains(Validate(new PipelineStage { StageId = "a", Name = "A", Kind = StageKind.Action, Tool = "x__y", ForEach = "nowhere.items" }), e => e.Contains("for each"));
        Assert.Contains(Validate(new PipelineStage { StageId = "s", Name = "S", Kind = StageKind.Script, Language = "ruby", Code = "x" }), e => e.Contains("language"));
        Assert.Contains(Validate(new PipelineStage { StageId = "s", Name = "S", Kind = StageKind.Script, Language = "python", Code = " " }), e => e.Contains("needs code"));
        Assert.Contains(Validate(new PipelineStage { StageId = "g", Name = "G" }), e => e.Contains("needs instructions"));
    }

    [Fact]
    public void Adding_and_changing_a_stage_keeps_its_kind_and_settings()
    {
        var current = new PipelineDefinition { Stages = [new PipelineStage { StageId = "research", Name = "Research", Instructions = "Research." }] };
        var added = PipelineEditor.Apply(current, [new PipelineEditOp
        {
            Op = PipelineEditOps.AddStage,
            After = "research",
            Stage = new PipelineStagePatch { Name = "Save", Kind = StageKind.Action, Tool = "sheets__append_rows", Arguments = """{ "rows": "{{ previous }}" }""" }
        }], new PipelineOptions());
        Assert.True(added.Success, string.Join(" ", added.Errors));
        var save = added.Pipeline!.Find("save")!;
        Assert.Equal(StageKind.Action, save.Kind);
        Assert.Equal(["research"], save.Inputs);

        var changed = PipelineEditor.Apply(added.Pipeline, [new PipelineEditOp
        {
            Op = PipelineEditOps.UpdateStage,
            StageId = "save",
            Stage = new PipelineStagePatch { ForEach = "previous.rows", Name = "Save rows" }
        }], new PipelineOptions());
        Assert.True(changed.Success, string.Join(" ", changed.Errors));
        Assert.Equal("previous.rows", changed.Pipeline!.Find("save")!.ForEach);
        Assert.Equal("sheets__append_rows", changed.Pipeline.Find("save")!.Tool);
    }

    [Fact]
    public void A_stage_can_be_colored_from_the_palette_and_uncolored_again()
    {
        var current = new PipelineDefinition { Stages = [new PipelineStage { StageId = "research", Name = "Research", Instructions = "Research." }] };
        PipelineDefinition Change(PipelineDefinition p, string color)
        {
            var r = PipelineEditor.Apply(p, [new PipelineEditOp { Op = PipelineEditOps.UpdateStage, StageId = "research", Stage = new PipelineStagePatch { Color = color } }], new PipelineOptions());
            Assert.True(r.Success, string.Join(" ", r.Errors));
            return r.Pipeline!;
        }

        var colored = Change(current, "Teal");
        Assert.Equal("teal", colored.Find("research")!.Color);
        Assert.Null(Change(colored, "").Find("research")!.Color);

        var bad = PipelineEditor.Apply(current, [new PipelineEditOp { Op = PipelineEditOps.UpdateStage, StageId = "research", Stage = new PipelineStagePatch { Color = "#ff0000" } }], new PipelineOptions());
        Assert.Contains(bad.Errors, e => e.Contains("unknown color"));
    }

    // ---- Forms ----

    private static FormDefinition Form() => new()
    {
        Title = "Request",
        Fields =
        [
            new FormField { Key = "email", Label = "Email", Type = FormFieldTypes.Email, Required = true },
            new FormField { Key = "budget", Label = "Budget", Type = FormFieldTypes.Number },
            new FormField { Key = "due", Label = "Due", Type = FormFieldTypes.Date },
            new FormField { Key = "areas", Label = "Areas", Type = FormFieldTypes.MultiSelect, Options = ["Web", "Mobile"] },
            new FormField { Key = "agree", Label = "I agree", Type = FormFieldTypes.Checkbox, Required = true },
            new FormField { Key = "site", Label = "Site", Type = FormFieldTypes.Url }
        ]
    };

    [Fact]
    public void Answers_are_typed()
    {
        var (answers, errors) = FormValidator.Check(Form(), (JsonObject)JsonNode.Parse("""
            { "email": " ama@example.com ", "budget": "1500.50", "due": "2026-11-01", "areas": ["Web", "Mobile", "Web"], "agree": true, "site": "" }
            """)!);

        Assert.Empty(errors);
        Assert.Equal("ama@example.com", answers!["email"]!.GetValue<string>());
        Assert.Equal(1500.50m, answers["budget"]!.GetValue<decimal>());
        Assert.Equal(["Web", "Mobile"], answers["areas"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.True(answers["agree"]!.GetValue<bool>());
        // Unanswered optional fields are there, as null, so scripts can read every key.
        Assert.True(answers.ContainsKey("site"));
        Assert.Null(answers["site"]);
        Assert.Contains("- Areas: Web, Mobile", FormValidator.Describe(Form(), answers));
    }

    [Fact]
    public void Wrong_answers_are_reported_per_field()
    {
        var (answers, errors) = FormValidator.Check(Form(), (JsonObject)JsonNode.Parse("""
            { "email": "ama", "budget": "lots", "due": "next week", "areas": ["TV"], "agree": false, "site": "javascript:alert(1)" }
            """)!);

        Assert.Null(answers);
        Assert.Equal(["agree", "areas", "budget", "due", "email", "site"], errors.Keys.Order().ToList());
    }

    [Theory]
    [InlineData("Bad Key", "text", "lowercase")]
    [InlineData("pick", "select", "options")]
    [InlineData("x", "color", "unknown type")]
    public void Bad_forms_are_refused(string key, string type, string expected)
    {
        var form = new FormDefinition { Fields = [new FormField { Key = key, Label = "Q", Type = type }] };
        Assert.Contains(expected, FormValidator.Validate(form));
    }

    [Fact]
    public void Duplicate_keys_and_empty_forms_are_refused()
    {
        Assert.Contains("at least one field", FormValidator.Validate(new FormDefinition()));
        Assert.Contains("Two fields", FormValidator.Validate(new FormDefinition { Fields = [new FormField { Key = "a", Label = "A" }, new FormField { Key = "a", Label = "B" }] }));
    }

    // ---- The script sandbox (needs Docker; set DOCKER_SCRIPT_TESTS=1) ----

    private static readonly bool Docker = Environment.GetEnvironmentVariable("DOCKER_SCRIPT_TESTS") == "1";

    private static DockerScriptRunner Runner() => new(Options.Create(new ScriptOptions()), NullLogger<DockerScriptRunner>.Instance);

    [Fact]
    public async Task Python_scripts_transform_data_and_their_prints_are_logs()
    {
        if (!Docker) return;
        var result = await Runner().RunAsync(new ScriptRunRequest
        {
            Language = ScriptLanguages.Python,
            Code = """
                print("rows:", len(data["previous"]["rows"]))
                return [r["Name"].upper() for r in data["previous"]["rows"]]
                """,
            DataJson = """{ "previous": { "rows": [ { "Name": "Ama" }, { "Name": "Kofi" } ] } }"""
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("""["AMA","KOFI"]""", result.OutputJson);
        Assert.Contains("rows: 2", result.Logs);
    }

    [Fact]
    public async Task JavaScript_scripts_can_await_and_return_objects()
    {
        if (!Docker) return;
        var result = await Runner().RunAsync(new ScriptRunRequest
        {
            Language = ScriptLanguages.JavaScript,
            Code = """
                const total = await Promise.resolve(data.input.items.reduce((s, i) => s + i.qty, 0));
                console.log("total", total);
                return { total, skus: data.input.items.map((i) => i.sku) };
                """,
            DataJson = """{ "input": { "items": [ { "sku": "MUG", "qty": 3 }, { "sku": "TEE", "qty": 4 } ] } }"""
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("""{"total":7,"skus":["MUG","TEE"]}""", result.OutputJson);
        Assert.Contains("total 7", result.Logs);
    }

    [Fact]
    public async Task Scripts_have_no_network_and_errors_say_where()
    {
        if (!Docker) return;
        var network = await Runner().RunAsync(new ScriptRunRequest
        {
            Language = ScriptLanguages.Python,
            Code = """
                import urllib.request
                return urllib.request.urlopen("http://example.com", timeout=3).status
                """,
            DataJson = "{}"
        });
        Assert.False(network.Ok);

        var error = await Runner().RunAsync(new ScriptRunRequest { Language = ScriptLanguages.Python, Code = "x = 1\nreturn data['missing']", DataJson = "{}" });
        Assert.False(error.Ok);
        Assert.Contains("KeyError (line 2)", error.Error);
    }

    [Fact]
    public async Task Scripts_that_run_too_long_are_stopped()
    {
        if (!Docker) return;
        var result = await Runner().RunAsync(new ScriptRunRequest { Language = ScriptLanguages.JavaScript, Code = "while (true) {}", DataJson = "{}", TimeoutSeconds = 3 });
        Assert.False(result.Ok);
        Assert.Contains("longer than 3s", result.Error);
    }
}
