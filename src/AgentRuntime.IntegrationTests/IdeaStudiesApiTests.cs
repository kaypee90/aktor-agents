using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Idea studies through the real API and Postgres (docs/studies.md, "Evaluating an idea"): an idea
/// with no sources runs on the mock model to a checked verdict (assumptions recorded and reviewed by
/// another agent, financial projections computed by the runtime), and a person's what-if runs the
/// same business model. Needs no analysis sandbox: projections are computed in the runtime.
/// </summary>
public sealed class IdeaStudiesApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task An_idea_is_evaluated_to_a_verdict_and_people_can_run_what_ifs()
    {
        if (Host.Unavailable is not null)
        {
            output.WriteLine("SKIPPED: " + Host.Unavailable);
            return;
        }

        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Ideas", Tenancy.TenantRole.Admin));
        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("IdeasOther"));

        // A brief is required, and checked.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/studies", new { name = "Empty", kind = "idea", idea = new { idea = " " } })).StatusCode);
        var created = await Json(await api.PostAsJsonAsync("/api/studies", new
        {
            name = "Maintenance triage",
            kind = "idea",
            idea = new
            {
                idea = "An AI assistant that triages tenants' maintenance requests for small landlords.",
                customers = "Independent landlords with 5 to 50 units",
                problem = "Requests arrive by text at all hours; landlords sort them by hand.",
                revenue_model = "Subscription at $39 per month per landlord",
                horizon_months = 36,
                currency = "usd"
            }
        }));
        var id = created.GetProperty("study_id").GetString()!;
        Assert.Equal("idea", created.GetProperty("kind").GetString());
        Assert.Equal("USD", created.GetProperty("idea").GetProperty("currency").GetString());
        Assert.Contains("profitable", created.GetProperty("question").GetString());

        // Another organization can't see it or run what-ifs on it.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/studies/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/studies/{id}/projections", new { name = "x", drivers = new { market_size = 1, price = 1 } })).StatusCode);

        // An idea runs from its brief alone.
        var run = await Json(await api.PostAsJsonAsync($"/api/studies/{id}/runs", new { }));
        var runId = run.GetProperty("task_id").GetString()!;
        var done = await Json(await api.GetAsync($"/api/tasks/{runId}/wait?timeout_seconds=240"));
        output.WriteLine(done.ToString());
        Assert.True(done.GetProperty("done").GetBoolean(), "the idea run finished");

        var detail = await Json(await api.GetAsync($"/api/studies/{id}"));
        output.WriteLine(detail.ToString());
        Assert.Equal("Completed", detail.GetProperty("status").GetString());

        // The register: the brief's price became an assumption; another agent reviewed them all.
        var assumptions = detail.GetProperty("assumptions").EnumerateArray().ToList();
        Assert.Equal(10, assumptions.Count);
        var price = assumptions.Single(a => a.GetProperty("name").GetString() == "price");
        Assert.Equal(39, price.GetProperty("likely").GetDouble());
        Assert.All(assumptions, a =>
        {
            Assert.Equal("accepted", a.GetProperty("status").GetString());
            Assert.NotEqual(a.GetProperty("agent_id").GetString(), a.GetProperty("reviewer").GetString());
        });

        // Two projections, computed by the runtime from the register.
        var projections = detail.GetProperty("projections").EnumerateArray().ToList();
        Assert.Equal(["base-case", "pessimistic"], projections.Select(p => p.GetProperty("name").GetString()));
        var baseCase = projections[0].GetProperty("result");
        Assert.Equal("assumption", baseCase.GetProperty("inputs").GetProperty("price").GetProperty("source").GetString());
        Assert.Equal(36, baseCase.GetProperty("monthly").GetProperty("cash").GetProperty("p50").GetArrayLength());
        var payback = baseCase.GetProperty("summary").GetProperty("p_payback").GetDouble();
        Assert.InRange(payback, 0, 1);
        Assert.True(projections[1].GetProperty("result").GetProperty("summary").GetProperty("p_payback").GetDouble() <= payback,
            "the pessimistic case pays back no more often than the base case");

        // The verdict, checked by the runtime and carrying the projection's numbers.
        var verdict = detail.GetProperty("report").GetProperty("content").GetProperty("verdict");
        Assert.Contains(verdict.GetProperty("recommendation").GetString(), new[] { "go", "go_with_changes", "no_go" });
        Assert.Equal(payback, verdict.GetProperty("projection").GetProperty("p_payback").GetDouble());
        Assert.Equal(Math.Round(payback * 10), verdict.GetProperty("profitability").GetProperty("score").GetDouble());
        Assert.Equal(10, verdict.GetProperty("assumption_check").GetProperty("accepted").GetInt32());

        // A person's what-if: the base case at a higher price, from the register plus one typed-in range.
        var drivers = new Dictionary<string, object>();
        foreach (var (name, input) in baseCase.GetProperty("inputs").EnumerateObject().Select(p => (p.Name, p.Value)))
        {
            if (input.GetProperty("source").GetString() == "assumption") drivers[name] = input.GetProperty("assumption_id").GetString()!;
        }

        drivers["price"] = new { low = 59, likely = 69, high = 79 };
        var whatIf = await Json(await api.PostAsJsonAsync($"/api/studies/{id}/projections", new { name = "price-69", revenue_model = "subscription", drivers }));
        Assert.True(whatIf.GetProperty("manual").GetBoolean());
        Assert.Equal("given", whatIf.GetProperty("result").GetProperty("inputs").GetProperty("price").GetProperty("source").GetString());
        Assert.True(whatIf.GetProperty("result").GetProperty("summary").GetProperty("npv").GetProperty("p50").GetDouble()
                    > baseCase.GetProperty("summary").GetProperty("npv").GetProperty("p50").GetDouble(), "a higher price raises NPV here");
        var evidence = await Json(await api.GetAsync($"/api/studies/{id}/evidence/{whatIf.GetProperty("evidence_id").GetString()}"));
        Assert.Equal("projection", evidence.GetProperty("kind").GetString());

        // Bad drivers are refused with the reason.
        var bad = await api.PostAsJsonAsync($"/api/studies/{id}/projections", new { name = "bad", drivers = new { market_size = 100, price = 10, monthly_churn = 4 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("0.05 for 5%", await bad.Content.ReadAsStringAsync());

        // The brief can be rewritten; a question study has none.
        var edited = await Json(await api.PatchAsJsonAsync($"/api/studies/{id}", new { idea = new { idea = "Triage plus vendor booking.", horizon_months = 48, currency = "USD" } }));
        Assert.Equal(48, edited.GetProperty("idea").GetProperty("horizon_months").GetInt32());
        var question = await Json(await api.PostAsJsonAsync("/api/studies", new { name = "Q", question = "Why?" }));
        Assert.Equal("question", question.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync($"/api/studies/{question.GetProperty("study_id").GetString()}", new { idea = new { idea = "x" } })).StatusCode);

        // Deleting the study removes its register and projections.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/studies/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/studies/{id}")).StatusCode);
    }
}
