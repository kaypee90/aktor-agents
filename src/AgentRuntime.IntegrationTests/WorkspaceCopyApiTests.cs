using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Cloning workspaces, exporting them as templates and deleting archived ones (docs/workspaces.md,
/// docs/templates.md): a clone gets the setup (pipeline, triggers, connections with their switched-off
/// tools, skills, knowledge) and none of the activity (runs, files, chat); a template made from a
/// workspace creates new workspaces like a built-in one, and travels as a file without secrets.
/// </summary>
public sealed class WorkspaceCopyApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task A_clone_copies_the_setup_but_not_the_activity_and_templates_and_deletion_work()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Copies", Tenancy.TenantRole.Admin));
        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("CopiesOther", Tenancy.TenantRole.Admin));

        // A workspace with everything set up: skills, knowledge, two triggers and a connection with a tool switched off.
        var source = (await Json(await api.PostAsJsonAsync("/api/workspaces", new { name = "Ops desk", goal = "Investigate production alerts." })))
            .GetProperty("workspace_id").GetString()!;
        await Json(await api.PostAsJsonAsync($"/api/skills?workspace={source}", new { name = "runbook", description = "Our runbook.", instructions = "Check the dashboards first." }));
        Assert.Equal(HttpStatusCode.NoContent, (await api.PostAsJsonAsync($"/api/memory?workspace={source}", new { key = "On-call", value = "Ama is on call this week." })).StatusCode);
        await Json(await api.PostAsJsonAsync($"/api/workspaces/{source}/triggers", new { kind = "schedule", name = "Hourly check", instruction = "Check for alerts.", every_minutes = 60 }));
        var hook = await Json(await api.PostAsJsonAsync($"/api/workspaces/{source}/triggers", new { kind = "webhook", name = "Alerts", instruction = "Investigate the alert." }));
        var connection = await Json(await api.PostAsJsonAsync($"/api/workspaces/{source}/connections", new { plugin_id = "demo-ops", name = "ops" }));
        var connectionId = connection.GetProperty("connection").GetProperty("connection_id").GetString();
        var tools = (await Json(await api.GetAsync($"/api/workspaces/{source}/connections"))).EnumerateArray().Single()
            .GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.True(tools.Count > 1);
        await Json(await api.PatchAsJsonAsync($"/api/workspaces/{source}/connections/{connectionId}", new { enabled_tools = tools.Skip(1).ToList() }));

        // Some activity: a run.
        var run = await Json(await api.PostAsJsonAsync($"/api/workspaces/{source}/runs", new { input = "Disk is full on db-1." }));
        await Json(await api.GetAsync($"/api/tasks/{run.GetProperty("run_id").GetString()}/wait?timeout_seconds=90"));

        // The clone.
        var clone = await Json(await api.PostAsJsonAsync($"/api/workspaces/{source}/clone", new { name = "Ops desk EU" }));
        output.WriteLine(clone.ToString());
        var copyId = clone.GetProperty("workspace_id").GetString()!;
        Assert.All(clone.GetProperty("connections").EnumerateArray(), c => Assert.True(c.GetProperty("ok").GetBoolean()));
        Assert.All(clone.GetProperty("triggers").EnumerateArray(), t => Assert.True(t.GetProperty("ok").GetBoolean()));
        Assert.Equal(1, clone.GetProperty("skills").GetInt32());
        Assert.Equal(1, clone.GetProperty("knowledge").GetInt32());
        var newHook = clone.GetProperty("triggers").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Alerts").GetProperty("webhook_path").GetString();
        Assert.NotNull(newHook);
        Assert.NotEqual(hook.GetProperty("webhook_path").GetString(), newHook);

        var copy = await Json(await api.GetAsync($"/api/workspaces/{copyId}"));
        Assert.Equal("Ops desk EU", copy.GetProperty("name").GetString());
        Assert.Equal("Investigate production alerts.", copy.GetProperty("goal").GetString());
        Assert.Equal(2, copy.GetProperty("triggers").GetArrayLength());
        Assert.Empty(copy.GetProperty("runs").EnumerateArray());
        var copiedTools = (await Json(await api.GetAsync($"/api/workspaces/{copyId}/connections"))).EnumerateArray().Single().GetProperty("tools").EnumerateArray().ToList();
        Assert.False(copiedTools.Single(t => t.GetProperty("name").GetString() == tools[0]).GetProperty("enabled").GetBoolean());
        Assert.Equal(["runbook"], (await Json(await api.GetAsync($"/api/skills?workspace={copyId}"))).EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(1, (await Json(await api.GetAsync($"/api/memory/summary?workspace={copyId}"))).GetProperty("facts").GetInt32());
        Assert.Empty((await Json(await api.GetAsync($"/api/workspaces/{copyId}/files"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/workspaces/{source}/clone", new { })).StatusCode);

        // Exported as a template: listed, downloadable, importable, and usable.
        var template = await Json(await api.PostAsJsonAsync($"/api/workspaces/{source}/export-template", new { name = "Ops desk", category = "Operations" }));
        var templateId = template.GetProperty("template_id").GetString()!;
        Assert.StartsWith("org-", templateId);
        var listed = (await Json(await api.GetAsync("/api/workspace-templates"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == templateId);
        Assert.True(listed.GetProperty("custom").GetBoolean());
        Assert.DoesNotContain((await Json(await other.GetAsync("/api/workspace-templates"))).EnumerateArray(), t => t.GetProperty("id").GetString() == templateId);
        var file = await (await api.GetAsync($"/api/workspace-templates/{templateId}/download")).Content.ReadAsStringAsync();
        Assert.Contains("\"Hourly check\"", file);
        var imported = await Json(await other.PostAsync("/api/workspace-templates/import", new StringContent(file, Encoding.UTF8, "application/json")));
        var made = await Json(await other.PostAsJsonAsync("/api/workspaces/from-template", new { template = imported.GetProperty("id").GetString(), name = "Their ops desk" }));
        Assert.Equal(2, made.GetProperty("triggers").GetArrayLength());
        Assert.True(made.GetProperty("connections")[0].GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/workspace-templates/import", new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "application/json"))).StatusCode);

        // Deleting: only once archived; its own knowledge and skills go, other workspaces are untouched.
        Assert.Equal(HttpStatusCode.Conflict, (await api.DeleteAsync($"/api/workspaces/{source}")).StatusCode);
        Assert.True((await api.PostAsync($"/api/workspaces/{source}/archive", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/workspaces/{source}")).StatusCode);
        Assert.DoesNotContain((await Json(await api.GetAsync("/api/workspaces"))).EnumerateArray(), w => w.GetProperty("workspace_id").GetString() == source);
        Assert.Equal(1, (await Json(await api.GetAsync($"/api/memory/summary?workspace={copyId}"))).GetProperty("facts").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/memory/summary?workspace={source}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/workspace-templates/{templateId}")).StatusCode);
    }
}
