using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Solutions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Integrating a solution into an application (docs/solutions.md#app-keys): an app key reaches one
/// solution through the apps API and nothing else (not the organization's API, not another
/// solution, not from a web page), issues end-user tokens, reads and writes records and runs
/// workflows; revoking it stops it; the templates open in the builder.
/// </summary>
public sealed class AppIntegrationApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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

    /// <summary>A solution that needs no model: the intake workflow's action stage opens a ticket.</summary>
    private static object Package(string name, IEnumerable<string>? origins = null) => new
    {
        bundle = new SolutionBundle
        {
            Manifest = $$$"""
                name: {{{name}}}
                title: Support desk
                version: 1.0.0
                entities:
                  Ticket:
                    fields:
                      subject: { type: string, required: true }
                      status: { type: enum, values: [open, closed], default: open }
                      requester: { type: end_user }
                workspaces:
                  - key: intake
                    name: Intake
                    goal: Open tickets from the input
                    data: { Ticket: write }
                    pipeline:
                      stages:
                        - stage_id: open
                          name: Open ticket
                          kind: Action
                          tool: data__create_ticket
                          arguments: { fields: { subject: "{{ input.subject }}" } }
                          retries: 0
                assistant:
                  name: Help bot
                  instructions: Help customers with their tickets.
                  data: { Ticket: write }
                  allowed_origins: [{{{string.Join(", ", origins ?? [])}}}]
                """
        }
    };

    private HttpClient Anonymous(string? bearer = null, string? origin = null)
    {
        var client = Host.Factory.CreateClient();
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null) client.DefaultRequestHeaders.Add("Origin", origin);
        return client;
    }

    private async Task<(Organization Org, string InstallId, string Key, string KeyId)> InstallWithKeyAsync(string orgName, string solution, IEnumerable<string>? origins = null)
    {
        var org = await Host.CreateOrganizationAsync(orgName, Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);
        var installId = (await Json(await api.PostAsJsonAsync("/api/v1/solutions", Package(solution, origins)))).GetProperty("install_id").GetString()!;
        var created = await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/app-keys", new { name = "backend" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var key = await Json(created);
        return (org, installId, key.GetProperty("key").GetString()!, key.GetProperty("key_id").GetString()!);
    }

    [Fact]
    public async Task An_app_key_reaches_its_one_solution_through_the_apps_api_and_nothing_else()
    {
        if (Skip()) return;
        var (org, installId, key, keyId) = await InstallWithKeyAsync("AppKeys", "desk");
        Assert.StartsWith(AppKeyFormat.Prefix, key);
        using var admin = Host.ClientFor(org);

        // Listed by its prefix, never the key itself.
        var listed = await admin.GetStringAsync($"/api/v1/solutions/{installId}/app-keys");
        Assert.Contains(keyId, listed);
        Assert.DoesNotContain(key, listed);

        // From a server it describes what the backend can use.
        using var server = Anonymous(key);
        var described = await Json(await server.GetAsync($"/api/v1/apps/{installId}"));
        Assert.Equal(["intake"], described.GetProperty("workflows").EnumerateArray().Select(w => w.GetString()!));
        Assert.Equal("backend", described.GetProperty("key").GetProperty("name").GetString());

        // From a web page it's refused: app keys belong on a server.
        using var page = Anonymous(key, origin: "https://app.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync($"/api/v1/apps/{installId}")).StatusCode);

        // To the organization's API it's no credential at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync("/api/v1/solutions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync($"/api/v1/solutions/{installId}/records/Ticket")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.PostAsJsonAsync($"/api/v1/solutions/{installId}/app-keys", new { name = "more" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync("/api/tasks")).StatusCode);
        // Nor is it an end user's token.
        Assert.Equal(HttpStatusCode.Forbidden, (await server.GetAsync($"/api/v1/apps/{installId}/conversations")).StatusCode);

        // It issues end-user tokens, and those can't do what the key does.
        var token = (await Json(await server.PostAsJsonAsync($"/api/v1/apps/{installId}/end-user-tokens", new { user_id = "user-ada", name = "Ada" })))
            .GetProperty("token").GetString()!;
        using var endUser = Anonymous(token);
        Assert.Equal("user-ada", (await Json(await endUser.GetAsync($"/api/v1/apps/{installId}"))).GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await endUser.PostAsJsonAsync($"/api/v1/apps/{installId}/runs", new { workflow = "intake", input = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await endUser.PostAsJsonAsync($"/api/v1/apps/{installId}/end-user-tokens", new { user_id = "someone-else" })).StatusCode);

        // Another solution's key doesn't reach this one, even with the right install id in the path.
        var (_, otherInstall, otherKey, _) = await InstallWithKeyAsync("OtherOrg", "desk");
        using var stranger = Anonymous(otherKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync($"/api/v1/apps/{installId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"/api/v1/apps/{otherInstall}")).StatusCode);

        // Members can't manage keys; revoking one stops it at once, and both are audited.
        using var member = Host.ClientFor(await Host.AddMemberAsync(org, "Member", Tenancy.TenantRole.Member));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/v1/solutions/{installId}/app-keys", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/solutions/{installId}/app-keys/{keyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync($"/api/v1/apps/{installId}")).StatusCode);
        var audit = await admin.GetStringAsync("/api/organization/policy/audit");
        Assert.Contains("solution.app_key.created", audit);
        Assert.Contains("solution.app_key.revoked", audit);
    }

    [Fact]
    public async Task A_backend_writes_records_and_runs_workflows_with_its_app_key()
    {
        if (Skip()) return;
        var (_, installId, key, _) = await InstallWithKeyAsync("AppBackend", "desk");
        using var server = Anonymous(key);
        var records = $"/api/v1/apps/{installId}/records/Ticket";

        // A retried create (same Idempotency-Key) returns the first record.
        HttpRequestMessage Create() => new(HttpMethod.Post, records)
        {
            Content = JsonContent.Create(new { subject = "Printer jam", requester = "user-ada" }),
            Headers = { { "Idempotency-Key", "ticket-1" } }
        };
        var first = await server.SendAsync(Create());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = (await Json(first)).GetProperty("id").GetString()!;
        var again = await server.SendAsync(Create());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, (await Json(again)).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await server.PostAsJsonAsync(records, new { status = "open" })).StatusCode);

        var changed = await Json(await server.PatchAsJsonAsync($"{records}/{id}", new { status = "closed" }));
        Assert.Equal("closed", changed.GetProperty("status").GetString());
        Assert.Equal(1, (await Json(await server.GetAsync($"{records}?where.status=closed"))).GetProperty("total").GetInt32());

        // A workflow run, followed to its result; a retried start returns the same run.
        HttpRequestMessage Run() => new(HttpMethod.Post, $"/api/v1/apps/{installId}/runs")
        {
            Content = JsonContent.Create(new { workflow = "intake", data = new { subject = "Printer on fire" } }),
            Headers = { { "Idempotency-Key", "run-1" } }
        };
        var started = await server.SendAsync(Run());
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var runId = (await Json(started)).GetProperty("run_id").GetString()!;
        Assert.Equal(runId, (await Json(await server.SendAsync(Run()))).GetProperty("run_id").GetString());

        var run = await Json(await server.GetAsync($"/api/v1/apps/{installId}/runs/{runId}?wait=45"));
        output.WriteLine(run.ToString());
        Assert.True(run.GetProperty("done").GetBoolean());
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal("intake", run.GetProperty("workflow").GetString());
        Assert.Equal(1, (await Json(await server.GetAsync($"{records}?where.subject=Printer on fire"))).GetProperty("total").GetInt32());

        // Unknown workflows say which exist; another solution's run isn't found through this one.
        var unknown = await server.PostAsJsonAsync($"/api/v1/apps/{installId}/runs", new { workflow = "nope", input = "x" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("intake", await unknown.Content.ReadAsStringAsync());
        var (_, otherInstall, otherKey, _) = await InstallWithKeyAsync("AppBackend2", "desk");
        using var stranger = Anonymous(otherKey);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/apps/{otherInstall}/runs/{runId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await server.DeleteAsync($"{records}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Allowed_origins_still_let_the_dashboard_preview_the_assistant()
    {
        if (Skip()) return;
        var (_, installId, key, _) = await InstallWithKeyAsync("Origins", "desk", ["https://app.example.com"]);
        using var server = Anonymous(key);
        var token = (await Json(await server.PostAsJsonAsync($"/api/v1/apps/{installId}/end-user-tokens", new { user_id = "u1" }))).GetProperty("token").GetString()!;
        var dashboard = Host.Factory.Services.GetRequiredService<IConfiguration>().GetSection("Cors:AllowedOrigins").Get<string[]>()?.FirstOrDefault() ?? "http://localhost:3000";

        using var allowed = Anonymous(token, "https://app.example.com");
        using var preview = Anonymous(token, dashboard);
        using var elsewhere = Anonymous(token, "https://evil.example.net");
        Assert.Equal(HttpStatusCode.OK, (await allowed.GetAsync($"/api/v1/apps/{installId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await preview.GetAsync($"/api/v1/apps/{installId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.GetAsync($"/api/v1/apps/{installId}")).StatusCode);
    }

    [Fact]
    public async Task The_templates_open_as_valid_packages()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Templates", Tenancy.TenantRole.Admin));
        var templates = await Json(await api.GetAsync("/api/v1/solutions/templates"));
        var names = templates.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.Contains("helpdesk", names);
        Assert.Contains("property-mgmt", names);

        foreach (var name in names)
        {
            var bundle = await Json(await api.GetAsync($"/api/v1/solutions/templates/{name}"));
            Assert.Contains($"name: {name}", bundle.GetProperty("manifest").GetString());
            var validation = await Json(await api.PostAsJsonAsync("/api/v1/solutions/validate", bundle));
            Assert.True(validation.GetProperty("ok").GetBoolean(), validation.ToString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/v1/solutions/templates/nope")).StatusCode);
    }
}
