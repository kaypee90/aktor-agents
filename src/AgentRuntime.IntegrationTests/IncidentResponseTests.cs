using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P9 end to end on the Mock provider, through the real API: a workspace from the
/// incident-response template receives an alert on its webhook, which runs its pipeline: triage,
/// investigators for logs, metrics and deploys in parallel, an incident report, and a proposed
/// rollback that waits for a human. Once approved, the rollback runs exactly once, the run's
/// result reaches the user, and every step is audited.
/// </summary>
public sealed class IncidentResponseTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForAsync(HttpClient api, string workspaceId, Func<JsonElement, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await Json(await api.GetAsync($"/api/workspaces/{workspaceId}"));
            if (condition(last)) return last;
            await Task.Delay(250);
        }

        throw new TimeoutException($"Waited for {what}. Conversation:\n" +
            string.Join("\n", last.GetProperty("conversation").EnumerateArray().Select(c => $"{c.GetProperty("author_name")}: {c.GetProperty("text")}")));
    }

    /// <summary>What finished runs reported in the chat.</summary>
    private static IEnumerable<string> RunResults(JsonElement ws) =>
        ws.GetProperty("conversation").EnumerateArray().Select(c => c.GetProperty("text").GetString()!).Where(t => t.Contains(" finished.") || t.Contains(" failed."));

    [Fact]
    public async Task Alert_to_investigation_report_and_approved_rollback()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Incident");
        using var api = Host.ClientFor(org);

        var created = await Json(await api.PostAsJsonAsync("/api/workspaces/from-template", new { template = "incident-response" }));
        var id = created.GetProperty("workspace_id").GetString()!;
        Assert.True(created.GetProperty("connections")[0].GetProperty("ok").GetBoolean(), created.ToString());
        var hookPath = created.GetProperty("webhooks")[0].GetProperty("path").GetString()!;

        var ws = await Json(await api.GetAsync($"/api/workspaces/{id}"));
        Assert.Equal("SemiAutonomous", ws.GetProperty("safety_policy").GetProperty("autonomy").GetString());
        Assert.Equal("incident-response", ws.GetProperty("template_id").GetString());
        var stages = ws.GetProperty("pipeline").GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("stage_id").GetString()).ToList();
        Assert.Equal(["triage", "logs", "metrics", "deploys", "diagnose", "remediate"], stages);

        // The alert arrives on the workspace's real webhook, as a monitoring system would send it.
        using var anonymous = Host.Factory.CreateClient();
        var delivered = await anonymous.PostAsync(hookPath, new StringContent(
            """{"alert":"HighErrorRate","service":"checkout-service","severity":"critical","value":"18.4%"}""", Encoding.UTF8, "application/json"));
        Assert.True(delivered.IsSuccessStatusCode, await delivered.Content.ReadAsStringAsync());

        ws = await WaitForAsync(api, id, w => w.GetProperty("approvals").EnumerateArray().Any(a => a.GetProperty("status").GetString() == "Pending"),
            "the rollback to wait for approval");

        // The alert started one run; its investigators ran and reported; the report was written before the proposal.
        var run = Assert.Single(ws.GetProperty("runs").EnumerateArray());
        Assert.Equal("webhook", run.GetProperty("source").GetString());
        var view = await Json(await api.GetAsync($"/api/workspaces/{id}/runs/{run.GetProperty("run_id").GetString()}"));
        var byStage = view.GetProperty("stages").EnumerateArray().ToDictionary(s => s.GetProperty("stage_id").GetString()!, s => s.GetProperty("status").GetString());
        Assert.All(new[] { "triage", "logs", "metrics", "deploys", "diagnose" }, s => Assert.Equal("Completed", byStage[s]));
        Assert.Equal("Running", byStage["remediate"]);
        // The file's history row is written by the event persister, a moment after the write.
        JsonElement files = default;
        for (var i = 0; i < 40; i++)
        {
            files = await Json(await api.GetAsync($"/api/workspaces/{id}/files"));
            if (files.EnumerateArray().Any(f => f.GetProperty("file_name").GetString() == "incident-report.md")) break;
            await Task.Delay(250);
        }

        Assert.Contains(files.EnumerateArray(), f => f.GetProperty("file_name").GetString() == "incident-report.md");

        var approval = ws.GetProperty("approvals").EnumerateArray().Single(a => a.GetProperty("status").GetString() == "Pending");
        Assert.Equal("ops__rollback_deploy", approval.GetProperty("tool_name").GetString());
        Assert.Contains("deploy #4812", approval.GetProperty("agent_note").GetString());
        var connectionId = ws.GetProperty("connections").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "ops").GetProperty("connection_id").GetString()!;
        Assert.False(DemoOpsPlugin.Rollbacks.TryGetValue(connectionId, out var none) && none.Count > 0, "nothing rolls back before approval");

        // A human approves; the rollback runs once and the run's result tells the user.
        await Json(await api.PostAsJsonAsync($"/api/workspaces/{id}/approvals/{approval.GetProperty("code").GetString()}/decision", new { approve = true, reason = "go" }));
        ws = await WaitForAsync(api, id, w => RunResults(w).Any(t => t.Contains("rolled back to v2.13.2")), "the user to be told about the rollback");
        Assert.Single(DemoOpsPlugin.Rollbacks[connectionId]);
        output.WriteLine(string.Join("\n", RunResults(ws)));

        // Every step is in the audit log, and the chain verifies.
        var audit = await Json(await api.GetAsync($"/api/workspaces/{id}/audit?limit=200"));
        var actions = audit.EnumerateArray().Select(e => $"{e.GetProperty("action").GetString()} {e.GetProperty("target").GetString()}").ToList();
        Assert.Contains(actions, a => a.StartsWith("approval.approved"));
        Assert.Contains(actions, a => a == "tool.call ops__rollback_deploy");
        Assert.Contains(actions, a => a == "tool.call ops__query_logs");
        Assert.True((await Json(await api.GetAsync($"/api/workspaces/{id}/audit/verify"))).GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Rejected_rollback_never_runs_and_simulate_alert_works()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("IncidentReject");
        using var api = Host.ClientFor(org);
        var id = (await Json(await api.PostAsJsonAsync("/api/workspaces/from-template", new { template = "incident-response", name = "Prod incidents" })))
            .GetProperty("workspace_id").GetString()!;
        var simulated = await api.PostAsync($"/api/workspaces/{id}/simulate-alert", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.True(simulated.IsSuccessStatusCode, await simulated.Content.ReadAsStringAsync());

        var ws = await WaitForAsync(api, id, w => w.GetProperty("approvals").EnumerateArray().Any(a => a.GetProperty("status").GetString() == "Pending"), "the approval");
        var approval = ws.GetProperty("approvals").EnumerateArray().Single(a => a.GetProperty("status").GetString() == "Pending");
        await Json(await api.PostAsJsonAsync($"/api/workspaces/{id}/approvals/{approval.GetProperty("approval_id").GetString()}/decision",
            new { approve = false, reason = "fixing forward instead" }));

        ws = await WaitForAsync(api, id, w => RunResults(w).Any(t => t.Contains("did not run")), "the user to be told it didn't run");
        var connectionId = ws.GetProperty("connections").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "ops").GetProperty("connection_id").GetString()!;
        Assert.False(DemoOpsPlugin.Rollbacks.TryGetValue(connectionId, out var rollbacks) && rollbacks.Count > 0);
    }
}
