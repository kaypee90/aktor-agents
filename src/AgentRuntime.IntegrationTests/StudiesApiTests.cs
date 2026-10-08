using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Studies through the real API, Postgres and the analysis sandbox (docs/studies.md): a dataset is
/// profiled and split, a run (on the mock model) assigns every source a role, fits a model that
/// another agent reviews, scores it on the sealed holdout, runs a simulation whose decisions
/// become a dataset, and submits a report the runtime checked. Needs Docker and the
/// aktor-analysis image (docker/analysis); skipped without them.
/// </summary>
public sealed class StudiesApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is not null)
        {
            output.WriteLine("SKIPPED: " + Host.Unavailable);
            return true;
        }

        if (!AnalysisImageExists())
        {
            output.WriteLine("SKIPPED: the aktor-analysis:1 image isn't built (docker build -t aktor-analysis:1 docker/analysis).");
            return true;
        }

        return false;
    }

    private static bool AnalysisImageExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "image inspect aktor-analysis:1") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Renewals that depend on the rent increase and tenure, with a seeded generator.</summary>
    private static byte[] RenewalsCsv()
    {
        var random = new Random(3);
        var sb = new StringBuilder("Rent Increase %,Tenure Years,Unit Type,Renewed\n");
        for (var i = 0; i < 200; i++)
        {
            var increase = Math.Round(random.NextDouble() * 12, 1);
            var tenure = Math.Round(random.NextDouble() * 10, 1);
            var unit = new[] { "studio", "1br", "2br" }[random.Next(3)];
            var z = 1.2 - 0.3 * increase + 0.25 * tenure;
            var renewed = random.NextDouble() < 1 / (1 + Math.Exp(-z)) ? 1 : 0;
            sb.Append(CultureInfo.InvariantCulture, $"{increase},{tenure},{unit},{renewed}\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static MultipartFormDataContent File(string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(part, "files", name);
        return form;
    }

    [Fact]
    public async Task A_study_runs_end_to_end_with_profiled_data_reviewed_models_a_simulation_and_a_checked_report()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Studies", Tenancy.TenantRole.Admin));
        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("StudiesOther"));

        var created = await Json(await api.PostAsJsonAsync("/api/studies", new { name = "Renewals", question = "What drives lease renewals?" }));
        var id = created.GetProperty("study_id").GetString()!;
        var workspaceId = created.GetProperty("workspace_id").GetString()!;
        Assert.StartsWith("study-", id);
        Assert.Equal("Draft", created.GetProperty("status").GetString());
        // A study's workspace isn't a workspace people manage, and nothing runs without a source.
        Assert.DoesNotContain((await Json(await api.GetAsync("/api/workspaces"))).EnumerateArray(), w => w.GetProperty("workspace_id").GetString() == workspaceId);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync($"/api/studies/{id}/runs", new { })).StatusCode);

        // The dataset: profiled, its column names cleaned, 20% sealed as holdout.
        var added = (await Json(await api.PostAsync($"/api/studies/{id}/datasets", File("Lease Renewals.csv", RenewalsCsv())))).EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, added.GetProperty("error").ValueKind);
        var dataset = added.GetProperty("dataset");
        Assert.Equal("lease_renewals", dataset.GetProperty("name").GetString());
        Assert.Equal(200, dataset.GetProperty("rows").GetInt32());
        Assert.Equal(40, dataset.GetProperty("holdout_rows").GetInt32());
        var columns = dataset.GetProperty("profile").GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["rent_increase_pct", "tenure_years", "unit_type", "renewed"], columns);
        // Not a dataset format: refused with a reason, nothing added.
        var refused = (await Json(await api.PostAsync($"/api/studies/{id}/datasets", File("notes.pdf", [1, 2, 3])))).EnumerateArray().Single();
        Assert.Contains("isn't a dataset format", refused.GetProperty("error").GetString());

        // The data dictionary, written by people.
        var datasetId = dataset.GetProperty("dataset_id").GetString()!;
        var edited = await Json(await api.PutAsJsonAsync($"/api/studies/{id}/datasets/{datasetId}", new { dictionary = new Dictionary<string, string> { ["renewed"] = "1 = the lease was renewed" } }));
        Assert.Equal("1 = the lease was renewed", edited.GetProperty("dictionary").GetProperty("renewed").GetString());

        // A run on the mock model, to the end.
        var run = await Json(await api.PostAsJsonAsync($"/api/studies/{id}/runs", new { instructions = "Focus on rent increases." }));
        var runId = run.GetProperty("task_id").GetString()!;
        Assert.StartsWith("srun-", runId);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync($"/api/studies/{id}/runs", new { })).StatusCode);
        var done = await Json(await api.GetAsync($"/api/tasks/{runId}/wait?timeout_seconds=240"));
        output.WriteLine(done.ToString());
        Assert.True(done.GetProperty("done").GetBoolean(), "the study run finished");

        var detail = await Json(await api.GetAsync($"/api/studies/{id}"));
        output.WriteLine(detail.ToString());
        Assert.Equal("Completed", detail.GetProperty("status").GetString());

        // Every source has a role; the model was reviewed by another agent and scored on the holdout.
        Assert.All(detail.GetProperty("data_use_plan").EnumerateArray(), p => Assert.NotEqual("unassigned", p.GetProperty("role").GetString()));
        var model = detail.GetProperty("models").EnumerateArray().Single();
        Assert.Equal("logistic_regression", model.GetProperty("method").GetString());
        Assert.Equal("renewed", model.GetProperty("target").GetString());
        Assert.Equal("accepted", model.GetProperty("status").GetString());
        Assert.NotEqual(model.GetProperty("author").GetString(), model.GetProperty("reviewer").GetString());
        var increase = model.GetProperty("result").GetProperty("coefficients").EnumerateArray().Single(c => c.GetProperty("term").GetString() == "rent_increase_pct");
        Assert.True(increase.GetProperty("coef").GetDouble() < 0, "a higher rent increase lowers the odds of renewal in the data");
        Assert.True(model.GetProperty("holdout").GetProperty("metrics").GetProperty("auc").GetDouble() > 0.5);
        Assert.Single(detail.GetProperty("hypotheses").EnumerateArray());

        // The simulation's decisions became a dataset.
        var simulation = detail.GetProperty("simulations").EnumerateArray().Single();
        Assert.Equal(6, simulation.GetProperty("participants").GetInt32());
        Assert.Equal(12, simulation.GetProperty("decisions").GetInt32());
        var simulated = detail.GetProperty("simulated_datasets").EnumerateArray().Single();
        Assert.Equal("simulated", simulated.GetProperty("kind").GetString());
        Assert.Equal(0, simulated.GetProperty("holdout_rows").GetInt32());

        // The report: checked by the runtime, with the data coverage table.
        var report = detail.GetProperty("report").GetProperty("content");
        Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => f.GetProperty("status").GetString() == "supported");
        Assert.Contains(report.GetProperty("data_coverage").EnumerateArray(), c => c.GetProperty("source").GetString() == "dataset:lease_renewals"
                                                                                  && c.GetProperty("evidence_count").GetInt32() > 0);

        // Evidence is inspectable, and the notebook reruns the analyses.
        var evidenceId = model.GetProperty("evidence_id").GetString()!;
        var evidence = await Json(await api.GetAsync($"/api/studies/{id}/evidence/{evidenceId}"));
        Assert.Equal("model", evidence.GetProperty("kind").GetString());
        var notebook = await (await api.GetAsync($"/api/studies/{id}/notebook")).Content.ReadAsStringAsync();
        Assert.Contains("sm.Logit", notebook);
        Assert.Contains("lease_renewals.parquet", notebook);

        // Analytics has the run under Studies, not under Tasks.
        var analytics = await Json(await api.GetAsync("/api/analytics?range=24h&scope=studies"));
        Assert.Equal(1, analytics.GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(1, analytics.GetProperty("simulations").GetProperty("count").GetInt32());
        Assert.True(analytics.GetProperty("evidence").GetProperty("findings_supported").GetInt32() >= 1);
        Assert.Equal(0, (await Json(await api.GetAsync("/api/analytics?range=24h"))).GetProperty("totals").GetProperty("runs").GetInt32());

        // Another organization sees none of it.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/studies/{id}")).StatusCode);
        Assert.Empty((await Json(await other.GetAsync("/api/studies"))).EnumerateArray());

        // Deleting removes it.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/studies/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/studies/{id}")).StatusCode);
    }
}
