using System.Text.Json.Nodes;
using AgentRuntime.Studies;

namespace AgentRuntime.Tests;

/// <summary>Idea studies (docs/studies.md, "Evaluating an idea"): the business model the runtime
/// simulates, how drivers are read, and what the runtime enforces on a verdict.</summary>
public sealed class IdeaStudyTests
{
    private static readonly IReadOnlyDictionary<string, StudyAssumption> NoAssumptions = new Dictionary<string, StudyAssumption>();

    private static ProjectionSpec Spec(JsonObject drivers, string model = "subscription", int horizon = 12, IReadOnlyDictionary<string, StudyAssumption>? assumptions = null)
    {
        var (parsed, errors) = BusinessModelSimulator.ParseDrivers(drivers, assumptions ?? NoAssumptions, model);
        Assert.True(parsed is not null, string.Join(" ", errors));
        return new ProjectionSpec { Name = "test", RevenueModel = model, HorizonMonths = horizon, Iterations = 200, Drivers = parsed! };
    }

    private static double D(JsonNode? n) => BusinessModelSimulator.Number(n) ?? throw new InvalidOperationException($"Not a number: {n}");

    [Fact]
    public void A_certain_subscription_adds_up_month_by_month()
    {
        // 100 customers from launch, nobody leaves: 1,000 a month in, 500 fixed out.
        var result = BusinessModelSimulator.Run(Spec(new JsonObject
        {
            ["market_size"] = 1_000_000, ["initial_customers"] = 100, ["price"] = 10, ["monthly_churn"] = 0,
            ["fixed_costs"] = 500, ["upfront_investment"] = 3000
        }));

        var monthly = result["monthly"]!;
        Assert.Equal(1000, D(monthly["revenue"]!["p50"]![0]));
        Assert.Equal(500, D(monthly["profit"]!["p50"]![11]));
        // -3,000 + 12 × 500.
        Assert.Equal(3000, D(monthly["cash"]!["p50"]![11]));
        var summary = result["summary"]!;
        Assert.Equal(1, D(summary["p_payback"]));
        // Cash is -500 after month 5 and 0 after month 6.
        Assert.Equal(6, D(summary["payback_month"]!["p50"]));
        Assert.Equal(3000, D(summary["funding_need"]!["p50"]));
        Assert.Equal(1, D(summary["first_profitable_month"]!["p50"]));
    }

    [Fact]
    public void Growth_stops_at_the_market_cap_and_one_time_buyers_pay_once()
    {
        // Marketing would bring 100 a month, but only 50 people (100 × 50%) would ever buy.
        var capped = BusinessModelSimulator.Run(Spec(new JsonObject
        {
            ["market_size"] = 100, ["adoption_rate"] = 0.5, ["price"] = 10, ["monthly_churn"] = 0, ["marketing_spend"] = 1000, ["cac"] = 10
        }, horizon: 6));
        Assert.Equal(50, D(capped["summary"]!["customers_at_end"]!["p50"]));
        Assert.Contains(capped["warnings"]!.AsArray(), w => w!.ToString().Contains("market cap"));

        // One-time: 10 new buyers a month, each pays once.
        var once = BusinessModelSimulator.Run(Spec(new JsonObject
        {
            ["market_size"] = 1_000_000, ["price"] = 100, ["marketing_spend"] = 500, ["cac"] = 50
        }, model: "one_time", horizon: 6));
        Assert.Equal(1000, D(once["monthly"]!["revenue"]!["p50"]![5]));
        Assert.Equal(10, D(once["summary"]!["customers_at_end"]!["p50"]));
    }

    [Fact]
    public void Nothing_is_sold_before_launch_but_fixed_costs_run()
    {
        var result = BusinessModelSimulator.Run(Spec(new JsonObject
        {
            ["market_size"] = 1000, ["initial_customers"] = 10, ["price"] = 10, ["fixed_costs"] = 100, ["launch_month"] = 3
        }));
        var revenue = result["monthly"]!["revenue"]!["p50"]!.AsArray();
        Assert.Equal(0, D(revenue[2]));
        Assert.Equal(100, D(revenue[3]));
        Assert.Equal(-300, D(result["monthly"]!["cash"]!["p50"]![2]));
    }

    [Fact]
    public void Uncertain_drivers_spread_the_outcomes_reproducibly_and_rank_by_impact()
    {
        var drivers = new JsonObject
        {
            ["market_size"] = 50_000, ["price"] = new JsonObject { ["low"] = 20, ["likely"] = 40, ["high"] = 60 },
            ["monthly_churn"] = new JsonObject { ["low"] = 0.02, ["likely"] = 0.03, ["high"] = 0.04 },
            ["marketing_spend"] = 5000, ["cac"] = 100, ["fixed_costs"] = 8000
        };
        var a = BusinessModelSimulator.Run(Spec(drivers, horizon: 36));
        var b = BusinessModelSimulator.Run(Spec(drivers, horizon: 36));
        Assert.Equal(a.ToJsonString(), b.ToJsonString());

        var npv = a["summary"]!["npv"]!;
        Assert.True(D(npv["p10"]) < D(npv["p50"]) && D(npv["p50"]) < D(npv["p90"]));
        var tornado = a["sensitivity"]!["drivers"]!.AsArray();
        Assert.Equal(2, tornado.Count);
        // The price range moves NPV more than the churn range here.
        Assert.Equal("price", tornado[0]!["driver"]!.ToString());
        Assert.True(D(tornado[0]!["swing"]) >= D(tornado[1]!["swing"]));
    }

    [Fact]
    public void Drivers_are_checked_and_assumptions_resolved()
    {
        var (missing, errors) = BusinessModelSimulator.ParseDrivers(new JsonObject { ["market_size"] = 100 }, NoAssumptions, "subscription");
        Assert.Null(missing);
        Assert.Contains(errors, e => e.StartsWith("price is required"));

        (_, errors) = BusinessModelSimulator.ParseDrivers(new JsonObject { ["market_size"] = 100, ["price"] = 5, ["monthly_churn"] = 5 }, NoAssumptions, "subscription");
        Assert.Contains(errors, e => e.Contains("0.05 for 5%"));

        (_, errors) = BusinessModelSimulator.ParseDrivers(new JsonObject { ["market_size"] = 100, ["price"] = 5, ["marketing_spend"] = 100 }, NoAssumptions, "subscription");
        Assert.Contains(errors, e => e.Contains("needs cac"));

        (_, errors) = BusinessModelSimulator.ParseDrivers(new JsonObject { ["market_size"] = 100, ["price"] = 5, ["revenue"] = 1 }, NoAssumptions, "subscription");
        Assert.Contains(errors, e => e.Contains("Unknown driver 'revenue'"));

        var assumption = new StudyAssumption
        {
            AssumptionId = "asm-00000001", StudyId = "study-1", AgentId = "agent-a", Name = "price", Low = 30, Likely = 40, High = 55, Evidence = ["ev-1"]
        };
        var (drivers, _) = BusinessModelSimulator.ParseDrivers(new JsonObject { ["market_size"] = 100, ["price"] = "asm-00000001" },
            new Dictionary<string, StudyAssumption> { [assumption.AssumptionId] = assumption }, "subscription");
        Assert.Equal((30, 40, 55, Driver.FromAssumption), (drivers!["price"].Low, drivers["price"].Likely, drivers["price"].High, drivers["price"].Source));
        Assert.Equal(Driver.Default, drivers["monthly_churn"].Source);
    }

    // ------------------------------------------------------------------ the verdict

    private static StudyEvidence Evidence(string id, string kind) => new() { EvidenceId = id, StudyId = "study-1", AgentId = "root-1", Kind = kind, Summary = "s" };

    private static StudyProjection Projection(string evidenceId, double payback) => new()
    {
        StudyId = "study-1", AgentId = "agent-f", Name = "base-case", EvidenceId = evidenceId,
        ResultJson = new JsonObject
        {
            ["currency"] = "USD", ["horizon_months"] = 36,
            ["summary"] = new JsonObject { ["p_payback"] = payback, ["npv"] = new JsonObject { ["p50"] = 1000 } },
            ["unsupported_drivers"] = new JsonArray()
        }.ToJsonString()
    };

    private static JsonObject Score(double score, params string[] evidence) => new()
    {
        ["score"] = score, ["summary"] = "why", ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e!).ToArray())
    };

    private static StudyReportValidator.Result Validate(JsonObject? verdict, double payback = 0.7, string kind = StudyKinds.Idea)
    {
        var report = new JsonObject
        {
            ["summary"] = "s",
            ["findings"] = new JsonArray(new JsonObject { ["claim"] = "c", ["evidence"] = new JsonArray("ev-prj") }),
            ["limitations"] = new JsonArray("l")
        };
        if (verdict is not null) report["verdict"] = verdict;
        var evidence = new[] { Evidence("ev-prj", EvidenceKinds.Projection), Evidence("ev-sim", EvidenceKinds.Simulation), Evidence("ev-doc", EvidenceKinds.Passage) };
        var assumption = new StudyAssumption { AssumptionId = "asm-1", StudyId = "study-1", AgentId = "agent-a", Name = "price", Low = 1, Likely = 2, High = 3, Status = "accepted", Evidence = ["ev-doc"] };
        return StudyReportValidator.Validate(new StudyReportValidator.Input(report, evidence.ToDictionary(e => e.EvidenceId),
            new Dictionary<string, StudyModel>(), new HashSet<string>(), new Dictionary<string, SourceRoleEntry>(), kind,
            new Dictionary<string, StudyAssumption> { ["asm-1"] = assumption },
            new Dictionary<string, StudyProjection> { ["ev-prj"] = Projection("ev-prj", payback) }));
    }

    private static JsonObject Verdict(double profitability = 7, string recommendation = "go", string profitEvidence = "ev-prj") => new()
    {
        ["recommendation"] = recommendation,
        ["feasibility"] = Score(6, "ev-doc"),
        ["profitability"] = Score(profitability, profitEvidence),
        ["usefulness"] = Score(8, "ev-sim"),
        ["key_assumptions"] = new JsonArray("asm-1")
    };

    [Fact]
    public void An_idea_report_needs_a_verdict_and_a_question_report_does_not()
    {
        Assert.Contains(Validate(null).Errors, e => e.Contains("needs a verdict"));
        Assert.True(Validate(null, kind: StudyKinds.Question).Ok);
    }

    [Fact]
    public void A_sound_verdict_is_accepted_with_the_runtimes_numbers()
    {
        var result = Validate(Verdict());
        Assert.True(result.Ok, string.Join(" ", result.Errors));
        var verdict = result.Report["verdict"]!;
        Assert.Equal(7, D(verdict["overall"]));
        Assert.Equal(0.7, D(verdict["projection"]!["p_payback"]));
        Assert.Equal(1, D(verdict["assumption_check"]!["accepted"]));
        Assert.True(result.Report["findings"]![0]!["projected"]!.GetValue<bool>());
    }

    [Fact]
    public void Profitability_must_rest_on_a_projection_that_supports_it()
    {
        Assert.Contains(Validate(Verdict(profitEvidence: "ev-doc")).Errors, e => e.Contains("must cite a financial projection"));
        Assert.Contains(Validate(Verdict(profitability: 9), payback: 0.2).Errors, e => e.Contains("pay back in at most"));
        Assert.Contains(Validate(Verdict(profitability: 2, recommendation: "no_go"), payback: 0.9).Warnings, e => e.Contains("though the cited projection"));
    }

    [Fact]
    public void Scores_need_evidence_and_ids_must_exist()
    {
        var verdict = Verdict();
        verdict["feasibility"] = Score(6);
        verdict["key_assumptions"] = new JsonArray("asm-404");
        verdict["recommendation"] = "maybe";
        var result = Validate(verdict);
        Assert.Contains(result.Errors, e => e.Contains("feasibility cites no evidence"));
        Assert.Contains(result.Errors, e => e.Contains("asm-404"));
        Assert.Contains(result.Errors, e => e.Contains("recommendation is one of"));

        var invented = Verdict();
        invented["usefulness"] = Score(5, "ev-nope");
        Assert.Contains(Validate(invented).Errors, e => e.Contains("ev-nope"));
    }

    [Fact]
    public void An_idea_brief_is_cleaned_and_checked()
    {
        Assert.Equal("Describe the idea.", IdeaBrief.Clean(new IdeaBrief { Idea = "  " }).Error);
        Assert.NotNull(IdeaBrief.Clean(new IdeaBrief { Idea = "x", HorizonMonths = 500 }).Error);
        Assert.NotNull(IdeaBrief.Clean(new IdeaBrief { Idea = "x", Currency = "dollars" }).Error);
        var (brief, error) = IdeaBrief.Clean(new IdeaBrief { Idea = " An app ", Currency = "eur", Customers = " " });
        Assert.Null(error);
        Assert.Equal(("An app", "EUR", (string?)null), (brief!.Idea, brief.Currency, brief.Customers));
        Assert.Equal(brief, IdeaBrief.Parse(brief.ToJson()));
        Assert.Equal("monthly_churn", RecordAssumptionTool.NameOf("Monthly churn %"));
    }
}
