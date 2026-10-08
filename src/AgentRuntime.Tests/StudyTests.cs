using System.Text.Json.Nodes;
using AgentRuntime.Studies;

namespace AgentRuntime.Tests;

/// <summary>Studies (docs/studies.md): what the runtime enforces on a report, and how experiments
/// are parsed, populated and summed up.</summary>
public sealed class StudyTests
{
    private static StudyEvidence Evidence(string id, string kind = EvidenceKinds.Query, string? source = "dataset:renewals") =>
        new() { EvidenceId = id, StudyId = "study-1", AgentId = "root-1", Kind = kind, Summary = "s", SourceKey = source };

    private static StudyModel Model(string id, string status, string evidenceId = "ev-model") => new()
    {
        ModelId = id, StudyId = "study-1", AgentId = "agent-a", Method = "logistic_regression", DatasetId = "d1",
        DatasetName = "renewals", Target = "renewed", EvidenceId = evidenceId, Status = status
    };

    private static SourceRoleEntry Role(string source, SourceRole role, string? reason = null) =>
        new() { StudyId = "study-1", SourceKey = source, Role = role, Reason = reason, AgentId = "root-1" };

    private static StudyReportValidator.Result Validate(JsonObject report, IEnumerable<StudyEvidence>? evidence = null,
        IEnumerable<StudyModel>? models = null, IEnumerable<string>? sources = null, IEnumerable<SourceRoleEntry>? roles = null) =>
        StudyReportValidator.Validate(new StudyReportValidator.Input(report,
            (evidence ?? [Evidence("ev-1"), Evidence("ev-model", EvidenceKinds.Model)]).ToDictionary(e => e.EvidenceId),
            (models ?? []).ToDictionary(m => m.ModelId),
            (sources ?? ["dataset:renewals"]).ToHashSet(),
            (roles ?? [Role("dataset:renewals", SourceRole.ModelInput)]).ToDictionary(r => r.SourceKey)));

    private static JsonObject Report(params JsonObject[] findings) => new()
    {
        ["summary"] = "Renewals drop as rent rises.",
        ["findings"] = new JsonArray(findings.Select(f => (JsonNode)f).ToArray()),
        ["limitations"] = new JsonArray("Observational data.")
    };

    private static JsonObject Finding(string claim, string[]? evidence = null, string[]? models = null) => new()
    {
        ["claim"] = claim,
        ["evidence"] = new JsonArray((evidence ?? []).Select(e => (JsonNode)e!).ToArray()),
        ["models"] = new JsonArray((models ?? []).Select(m => (JsonNode)m!).ToArray())
    };

    [Fact]
    public void A_report_whose_findings_cite_real_evidence_is_accepted_and_annotated()
    {
        var result = Validate(Report(Finding("Higher increases, fewer renewals.", ["ev-1"]), Finding("Tenants value stability.")));
        Assert.True(result.Ok, string.Join(" ", result.Errors));
        var findings = result.Report["findings"]!.AsArray();
        Assert.Equal("supported", findings[0]!["status"]!.ToString());
        Assert.Equal("interpretation", findings[1]!["status"]!.ToString());
        Assert.Equal("low", findings[1]!["confidence"]!.ToString());
        Assert.Contains(result.Warnings, w => w.Contains("interpretation"));
    }

    [Fact]
    public void Invented_evidence_and_unreviewed_models_are_refused()
    {
        var invented = Validate(Report(Finding("x", ["ev-nope"])));
        Assert.False(invented.Ok);
        Assert.Contains(invented.Errors, e => e.Contains("ev-nope"));

        var unreviewed = Validate(Report(Finding("x", models: ["mdl-1"])), models: [Model("mdl-1", "candidate")]);
        Assert.Contains(unreviewed.Errors, e => e.Contains("mdl-1 (candidate)"));

        var accepted = Validate(Report(Finding("x", models: ["mdl-1"])), models: [Model("mdl-1", "accepted")]);
        Assert.True(accepted.Ok, string.Join(" ", accepted.Errors));
        // A model's own fit counts as the finding's evidence.
        Assert.Equal("supported", accepted.Report["findings"]![0]!["status"]!.ToString());
    }

    [Fact]
    public void Every_source_needs_a_role_and_not_relevant_needs_a_reason()
    {
        var missing = Validate(Report(Finding("x", ["ev-1"])), sources: ["dataset:renewals", "document:report.pdf"]);
        Assert.Contains(missing.Errors, e => e.Contains("document:report.pdf"));

        var unexplained = Validate(Report(Finding("x", ["ev-1"])), sources: ["dataset:renewals", "document:report.pdf"],
            roles: [Role("dataset:renewals", SourceRole.ModelInput), Role("document:report.pdf", SourceRole.NotRelevant)]);
        Assert.Contains(unexplained.Errors, e => e.Contains("need a reason"));

        var explained = Validate(Report(Finding("x", ["ev-1"])), sources: ["dataset:renewals", "document:report.pdf"],
            roles: [Role("dataset:renewals", SourceRole.ModelInput), Role("document:report.pdf", SourceRole.NotRelevant, "About another market.")]);
        Assert.True(explained.Ok, string.Join(" ", explained.Errors));
    }

    [Fact]
    public void Findings_resting_on_a_simulation_are_marked_simulated()
    {
        var result = Validate(Report(Finding("Half would leave.", ["ev-sim"])),
            evidence: [Evidence("ev-sim", EvidenceKinds.Simulation, "dataset:renewals,dataset:sim_rent")]);
        Assert.True(result.Report["findings"]![0]!["simulated"]!.GetValue<bool>());
    }

    private static JsonObject Experiment() => JsonNode.Parse("""
        {
          "name": "Rent +8%",
          "population": { "size": 10, "evidence": ["ev-1"], "segments": [
            { "name": "long", "share": 0.7, "description": "You've rented here for years.", "attributes": { "tenure": "long" }, "ranges": { "income_k": [40, 90] } },
            { "name": "new", "share": 0.3, "description": "You moved in last year." } ] },
          "conditions": [ { "name": "control", "scenario": "Rent stays the same." }, { "name": "increase", "scenario": "Rent rises 8%." } ],
          "decision": { "question": "Do you renew?", "options": ["renew", "leave"] },
          "rounds": 9, "replications": 2,
          "calibration": { "option": "leave", "rate": 12, "evidence_id": "ev-1" }
        }
        """)!.AsObject();

    [Fact]
    public void An_experiment_is_parsed_with_the_runtime_limits_and_must_cite_its_data()
    {
        var limits = new StudyOptions();
        var (spec, errors, notes) = ExperimentRunner.Parse(Experiment(), limits);
        Assert.Empty(errors);
        Assert.Equal(limits.MaxRounds, spec!.Rounds);
        Assert.Contains(notes, n => n.Contains("Rounds capped"));
        Assert.Equal(0.12, spec.Calibration!.Value.Rate, 6);
        Assert.Equal(["ev-1"], spec.CitedEvidence);

        var uncited = Experiment();
        uncited["population"]!["evidence"] = new JsonArray();
        Assert.Contains(ExperimentRunner.Parse(uncited, limits).Errors, e => e.Contains("built from the study's data"));

        var badCalibration = Experiment();
        badCalibration["calibration"]!["option"] = "move";
        Assert.Contains(ExperimentRunner.Parse(badCalibration, limits).Errors, e => e.Contains("isn't one of"));
    }

    [Fact]
    public void A_population_follows_the_segment_shares_and_is_the_same_for_the_same_seed()
    {
        var (spec, _, _) = ExperimentRunner.Parse(Experiment(), new StudyOptions());
        var people = ExperimentRunner.GeneratePopulation(spec!, replication: 1);
        Assert.Equal(10, people.Count);
        Assert.Equal(7, people.Count(p => p.Segment == "long"));
        Assert.All(people.Where(p => p.Segment == "long"), p => Assert.InRange(double.Parse(p.Attributes["income_k"]), 40, 90));
        Assert.Equal(people.Select(p => p.Attributes.GetValueOrDefault("income_k")), ExperimentRunner.GeneratePopulation(spec!, 1).Select(p => p.Attributes.GetValueOrDefault("income_k")));
        Assert.NotEqual(people.Select(p => p.Attributes.GetValueOrDefault("income_k")), ExperimentRunner.GeneratePopulation(spec!, 2).Select(p => p.Attributes.GetValueOrDefault("income_k")));
    }

    [Fact]
    public void A_summary_reports_shares_effects_the_calibration_gap_and_low_diversity()
    {
        var (spec, _, _) = ExperimentRunner.Parse(Experiment(), new StudyOptions());
        var rows = new List<Dictionary<string, object?>>();
        void Add(string condition, string choice, int n)
        {
            for (var i = 0; i < n; i++) rows.Add(new() { ["condition"] = condition, ["round"] = 1, ["choice"] = choice });
        }

        Add("control", "renew", 10);
        Add("increase", "renew", 6);
        Add("increase", "leave", 4);
        var summary = ExperimentRunner.Summarize(spec! with { Rounds = 1 }, rows);

        var effect = summary["effects"]![0]!;
        Assert.Equal(0.4, effect["difference"]!["leave"]!.GetValue<double>(), 6);
        var calibration = summary["calibration"]!;
        Assert.Equal(-0.12, calibration["gap"]!.GetValue<double>(), 6);
        Assert.False(calibration["calibrated"]!.GetValue<bool>());
        var warnings = summary["warnings"]!.AsArray().Select(w => w!.ToString()).ToList();
        Assert.Contains(warnings, w => w.StartsWith("Low diversity: in 'control'"));
        Assert.Contains(warnings, w => w.StartsWith("Calibration gap"));
    }

    [Theory]
    [InlineData("Tenant Renewals 2024.xlsx", "tenant_renewals_2024")]
    [InlineData("2024-sales.csv", "t_2024_sales")]
    [InlineData("%%%.csv", "dataset")]
    public void Dataset_files_become_table_names(string file, string table) => Assert.Equal(table, StudyIngest.TableNameFor(file));

    [Fact]
    public void Study_paths_refuse_anything_but_study_ids()
    {
        var options = new StudyOptions { DataRoot = "/data/studies" };
        Assert.EndsWith("study-abc123", StudyPaths.StudyRoot(options, "study-abc123"));
        Assert.Throws<ArgumentException>(() => StudyPaths.StudyRoot(options, "../etc"));
        Assert.Throws<ArgumentException>(() => StudyPaths.StudyRoot(options, "study-../x"));
        Assert.Throws<ArgumentException>(() => StudyPaths.DatasetDir(options, "study-abc", "../../x"));
    }
}
