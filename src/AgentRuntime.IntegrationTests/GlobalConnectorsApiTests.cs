using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Solutions;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Global connectors and the solution designer through the real API: a connector is set up once,
/// a solution deploys onto it without asking for secrets (its workspace and its assistant both use
/// it), a rotated secret reaches them, it can't be deleted from under them by accident, and another
/// organization doesn't see it. The builder's conversation proposes validated changes (Mock model).
/// </summary>
public sealed class GlobalConnectorsApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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

    /// <summary>A remote plugin service that records the Authorization header of every call.</summary>
    private static (HttpListener Service, int Port, List<string> Auths) RemoteService()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var service = new HttpListener();
        service.Prefixes.Add($"http://127.0.0.1:{port}/");
        service.Start();
        var auths = new List<string>();
        _ = Task.Run(async () =>
        {
            while (service.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await service.GetContextAsync(); } catch { return; }
                lock (auths) auths.Add($"{ctx.Request.Url!.AbsolutePath} {ctx.Request.Headers["Authorization"]}");
                var body = ctx.Request.Url.AbsolutePath switch
                {
                    "/validate" => """{"ok":true,"message":"hi"}""",
                    "/tools/list" => """{"tools":[{"name":"lookup","side_effects":"read_only"}]}""",
                    _ => """{"ok":true,"result":{}}"""
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        return (service, port, auths);
    }

    [Fact]
    public async Task A_solution_deploys_onto_a_global_connector_without_asking_for_secrets()
    {
        if (Skip()) return;
        var (service, port, auths) = RemoteService();
        using var _ = service;
        var org = await Host.CreateOrganizationAsync("SharedCrm", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);

        // Members can't create one; a key that doesn't connect isn't saved.
        using (var member = Host.ClientFor(await Host.AddMemberAsync(org, "Member", Tenancy.TenantRole.Member)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/connectors", new { name = "crm", plugin_id = "remote-plugin" })).StatusCode);
        }

        var created = await Json(await api.PostAsJsonAsync("/api/connectors", new
        {
            name = "Team CRM",
            plugin_id = "remote-plugin",
            description = "The sales CRM",
            settings = new { base_url = $"http://127.0.0.1:{port}" },
            secrets = new { token = "first-token" }
        }));
        var connectorId = created.GetProperty("connector_id").GetString()!;
        Assert.StartsWith("gc-", connectorId);
        Assert.Equal("team_crm", created.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync("/api/connectors", new
        {
            name = "team_crm", plugin_id = "remote-plugin", settings = new { base_url = $"http://127.0.0.1:{port}" }, secrets = new { token = "x" }
        })).StatusCode);

        var manifest = """
            name: shared-crm-app
            version: 1.0.0
            connectors:
              - name: crm
                plugin: remote-plugin
            workspaces:
              - key: desk
                name: Desk
                goal: Answer CRM questions.
                connectors: [crm]
            assistant:
              instructions: Look customers up.
              connectors: [crm]
            """;
        var package = new SolutionBundle { Manifest = manifest };
        // Validation offers the global connector for the crm connector.
        var validation = await Json(await api.PostAsJsonAsync("/api/v1/solutions/validate", package));
        Assert.Equal("team_crm", validation.GetProperty("globals").GetProperty("crm")[0].GetProperty("name").GetString());

        // Without secrets or a global connector, it's refused; with the global connector, no secrets are asked for.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = package })).StatusCode);
        var install = await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = package, globals = new { crm = "team_crm" } }));
        var installId = install.GetProperty("install_id").GetString()!;
        var workspaceId = install.GetProperty("workspaces")[0].GetProperty("workspace_id").GetString()!;

        var wsCrm = (await Json(await api.GetAsync($"/api/workspaces/{workspaceId}/connections"))).EnumerateArray().Single(c => c.GetProperty("name").GetString() == "crm");
        Assert.Equal(connectorId, wsCrm.GetProperty("global_connector_id").GetString());
        Assert.Equal("team_crm", wsCrm.GetProperty("global_connector_name").GetString());
        lock (auths) Assert.Contains("/tools/list Bearer first-token", auths);

        // The installed solution shows which connector backs it; an upgrade keeps it without being told again.
        Assert.Equal("team_crm", (await Json(await api.GetAsync($"/api/v1/solutions/{installId}"))).GetProperty("connectors")[0].GetProperty("global").GetString());
        await Json(await api.PutAsJsonAsync($"/api/v1/solutions/{installId}", new { bundle = new SolutionBundle { Manifest = manifest.Replace("1.0.0", "1.0.1") } }));

        // The assistant's conversations link it too (an admin chose it, though it isn't open to tasks).
        var token = (await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/end-user-tokens", new { user_id = "u1" }))).GetProperty("token").GetString()!;
        using (var client = Host.Factory.CreateClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var conversation = (await Json(await client.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "Who is ada@example.com?" })))
                .GetProperty("conversation_id").GetString()!;
            var taskCrm = (await Json(await api.GetAsync($"/api/tasks/{conversation}/connections"))).EnumerateArray().Single(c => c.GetProperty("name").GetString() == "crm");
            Assert.Equal(connectorId, taskCrm.GetProperty("global_connector_id").GetString());
        }

        // It lists where it's used; a rotated token reaches the links, which are refreshed.
        var listed = (await Json(await api.GetAsync("/api/connectors"))).EnumerateArray().Single();
        Assert.Contains(listed.GetProperty("used_by").EnumerateArray(), u => u.GetProperty("owner_id").GetString() == workspaceId);
        var rotated = await Json(await api.PatchAsJsonAsync($"/api/connectors/{connectorId}", new { secrets = new { token = "second-token" } }));
        Assert.True(rotated.GetProperty("refreshed_connections").GetInt32() >= 1);
        lock (auths) Assert.Contains("/tools/list Bearer second-token", auths);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync($"/api/connectors/{connectorId}", new { name = "renamed" })).StatusCode);

        // Another organization can't see it, link it, or deploy onto it.
        using (var other = Host.ClientFor(await Host.CreateOrganizationAsync("Snoop", Tenancy.TenantRole.Admin)))
        {
            Assert.Empty((await Json(await other.GetAsync("/api/connectors"))).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/connectors/{connectorId}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync("/api/v1/solutions", new { bundle = package, globals = new { crm = connectorId } })).StatusCode);
        }

        // Deleting it while used is refused, unless forced, which removes the links too.
        Assert.Equal(HttpStatusCode.Conflict, (await api.DeleteAsync($"/api/connectors/{connectorId}")).StatusCode);
        var deleted = await Json(await api.DeleteAsync($"/api/connectors/{connectorId}?force=true"));
        Assert.True(deleted.GetProperty("removed_connections").GetInt32() >= 2);
        Assert.DoesNotContain((await Json(await api.GetAsync($"/api/workspaces/{workspaceId}/connections"))).EnumerateArray(),
            c => c.GetProperty("name").GetString() == "crm");
        service.Stop();
    }

    [Fact]
    public async Task The_builder_conversation_proposes_validated_changes_and_saves_nothing()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Designer", Tenancy.TenantRole.Member));
        // As the builder sends it (keys verbatim: the test client would camel-case an anonymous object's).
        var draft = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "name": "my-app", "title": "My app", "version": "0.1.0",
              "entities": { "Request": { "fields": { "subject": { "type": "string", "required": true } } } },
              "assistant": { "instructions": "Help customers.", "data": { "Request": "write" } }
            }
            """)!;

        var proposal = await Json(await api.PostAsJsonAsync("/api/v1/solutions/design", new
        {
            manifest = draft,
            skills = Array.Empty<object>(),
            history = new[] { new { role = "user", content = "I run a property company." } },
            request = "Add a Vendor entity with name, phone and hourly rate"
        }));
        Assert.True(proposal.GetProperty("can_apply").GetBoolean(), proposal.ToString());
        Assert.Contains("Added entity Vendor", proposal.GetProperty("changes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("number", proposal.GetProperty("manifest").GetProperty("entities").GetProperty("Vendor").GetProperty("fields")
            .GetProperty("hourly_rate").GetProperty("type").GetString());
        Assert.True(proposal.GetProperty("problems").GetArrayLength() == 0, proposal.GetProperty("problems").ToString());
        Assert.False(string.IsNullOrEmpty(proposal.GetProperty("reply").GetString()));
        Assert.Empty((await Json(await api.GetAsync("/api/v1/solutions"))).EnumerateArray()); // nothing installed or saved

        var empty = await Json(await api.PostAsJsonAsync("/api/v1/solutions/design", new { manifest = draft, request = "" }));
        Assert.False(empty.GetProperty("can_apply").GetBoolean());
        Assert.NotEmpty(empty.GetProperty("errors").EnumerateArray());
    }
}
