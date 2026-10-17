using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Solutions;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Solutions end to end through the real API (docs/solutions.md): a package installs into an
/// organization with its workspaces, records and connections; records written by the builder's
/// backend fire record triggers and signed webhooks; upgrades change only what the package
/// changed; evals run for real; end users reach only their own conversations and records; and
/// nothing crosses organizations.
/// </summary>
public sealed class SolutionsApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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

    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "AktorAgents.slnx"))) root = Path.GetDirectoryName(root);
        return root!;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>A package with a records-driven workspace that needs no model: a record trigger
    /// starts a run whose action stage writes back to the record. Without <paramref name="withIntake"/>
    /// (a later version) its second workspace and the evals on it are gone.</summary>
    private static SolutionBundle HelpdeskBundle(string version, string instructions = "Mark it triaged.", int webhookPort = 0, bool withIntake = true)
    {
        var intake = """
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
            """;
        var evals = """
            evals:
              - name: intake opens a ticket
                workspace: intake
                input: open one
                input_data: { subject: Printer on fire }
                timeout_seconds: 60
                expect:
                  status: Completed
                  records:
                    Ticket: { at_least: 1, where: { status: open, subject: Printer on fire } }
              - name: impossible expectation
                workspace: intake
                input: open one
                input_data: { subject: Something }
                timeout_seconds: 60
                expect:
                  records:
                    Ticket: { at_least: 5 }
            """;
        var webhooks = webhookPort > 0 ? $"""
            webhooks:
              - url: http://127.0.0.1:{webhookPort}/hooks/aktor
                events: [record.created]
                entities: [Ticket]
            """ : string.Empty;
        var manifest = $$$"""
            name: helpdesk
            title: Helpdesk
            version: {{{version}}}
            inputs:
              - { key: team, default: Support }
            entities:
              Customer:
                fields:
                  email: { type: string, required: true }
                  plan: { type: enum, values: [free, pro], default: free }
                  owner: { type: end_user }
              Ticket:
                fields:
                  customer: { type: ref, entity: Customer }
                  subject: { type: string, required: true }
                  status: { type: enum, values: [open, triaged, closed], default: open }
                  requester: { type: end_user }
            workspaces:
              - key: desk
                name: Desk
                goal: Triage tickets
                data: { Ticket: write, Customer: read }
                pipeline:
                  stages:
                    - stage_id: mark
                      name: Mark triaged
                      kind: Action
                      tool: data__update_ticket
                      arguments:
                        id: "{{ input.record_id }}"
                        fields: { status: triaged }
                      retries: 0
                triggers:
                  - kind: Record
                    name: New ticket
                    entity: Ticket
                    on: [created]
                    instruction: {{{instructions}}}
            {{{(withIntake ? intake : string.Empty)}}}
            assistant:
              name: Help bot
              greeting: How can I help?
              instructions: Help customers with their tickets.
              data: { Ticket: write, Customer: read }
            seed:
              Customer:
                - { id: c-1, email: ada@example.com, plan: pro, owner: user-ada }
                - { id: c-2, email: bob@example.com, owner: user-bob }
              Ticket:
                - { subject: Old issue, customer: c-1, status: closed, requester: user-ada }
            {{{(withIntake ? evals : string.Empty)}}}
            {{{webhooks}}}
            """;
        return new SolutionBundle { Manifest = manifest };
    }

    private static object HelpdeskPackage(string version, string instructions = "Mark it triaged.", int webhookPort = 0, bool withIntake = true) =>
        new { bundle = HelpdeskBundle(version, instructions, webhookPort, withIntake) };

    [Fact]
    public async Task The_sample_package_installs_with_its_workspaces_records_watch_and_knowhow()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("PropertyCo", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);
        var bundle = SolutionPackage.FromDirectory(Path.Combine(RepoRoot(), "samples", "solutions", "property-mgmt"));

        var validation = await Json(await api.PostAsJsonAsync("/api/v1/solutions/validate", bundle));
        Assert.True(validation.GetProperty("ok").GetBoolean(), validation.ToString());

        var installed = await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle }));
        output.WriteLine(installed.ToString());
        var installId = installed.GetProperty("install_id").GetString()!;
        Assert.Equal(3, installed.GetProperty("seeded_records").GetInt32());
        var workspaces = installed.GetProperty("workspaces").EnumerateArray().ToList();
        Assert.Equal(2, workspaces.Count);
        foreach (var w in workspaces)
        {
            Assert.All(w.GetProperty("connections").EnumerateArray(), c => Assert.True(c.GetProperty("ok").GetBoolean(), c.ToString()));
            Assert.All(w.GetProperty("triggers").EnumerateArray(), t => Assert.True(t.GetProperty("ok").GetBoolean(), t.ToString()));
        }

        // The records connection gives each workspace exactly the tools its data access allows.
        var desk = workspaces.Single(w => w.GetProperty("key").GetString() == "maintenance-desk").GetProperty("workspace_id").GetString()!;
        var tools = (await Json(await api.GetAsync($"/api/workspaces/{desk}/connections"))).EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "data").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        Assert.Contains("data__update_ticket", tools);
        Assert.Contains("data__list_lease", tools);
        Assert.DoesNotContain("data__update_lease", tools);
        Assert.DoesNotContain("data__create_property", tools);

        // Seed records reference each other by real ids, and the lease that ends soon is in the
        // renewal watch's window.
        var leases = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Lease?filter=ends_on<=2026-12-31&order_by=unit&desc=false"));
        Assert.Equal(1, leases.GetProperty("total").GetInt32());
        var properties = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Property"));
        Assert.Equal(properties.GetProperty("items")[0].GetProperty("id").GetString(), leases.GetProperty("items")[0].GetProperty("property").GetString());

        // By name works as well as by id; the view lists entities with their counts.
        var view = await Json(await api.GetAsync("/api/v1/solutions/property-mgmt"));
        Assert.Equal(installId, view.GetProperty("install_id").GetString());
        Assert.Equal(2, view.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Lease").GetProperty("records").GetInt32());

        // Skills and knowledge landed in the workspaces.
        var skills = await Json(await api.GetAsync($"/api/skills?workspace={desk}"));
        Assert.Contains(skills.EnumerateArray(), s => s.GetProperty("name").GetString() == "maintenance-triage");

        // Installing twice is refused; another organization doesn't see it.
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync("/api/v1/solutions", new { bundle })).StatusCode);
        using var other = Host.ClientFor(await Host.CreateOrganizationAsync("Snoop", Tenancy.TenantRole.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/solutions/{installId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/solutions/{installId}/records/Lease")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Host.ClientFor(await Host.AddMemberAsync(org, "Member", Tenancy.TenantRole.Member))
            .PostAsJsonAsync("/api/v1/solutions", new { bundle })).StatusCode);
    }

    [Fact]
    public async Task Records_fire_record_triggers_and_signed_webhooks_and_upgrades_and_evals_work()
    {
        if (Skip()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var hooks = new HttpListener();
        hooks.Prefixes.Add($"http://127.0.0.1:{port}/hooks/");
        hooks.Start();
        var received = new List<(string Body, string Signature)>();
        _ = Task.Run(async () =>
        {
            while (hooks.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await hooks.GetContextAsync();
                }
                catch
                {
                    return;
                }

                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                lock (received) received.Add((body, ctx.Request.Headers["X-Aktor-Signature"] ?? string.Empty));
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        });

        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Helpdesk", Tenancy.TenantRole.Admin));
        var installed = await Json(await api.PostAsJsonAsync("/api/v1/solutions", HelpdeskPackage("1.0.0", webhookPort: port)));
        var installId = installed.GetProperty("install_id").GetString()!;
        var secret = installed.GetProperty("webhook_secret").GetString()!;
        var customers = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Customer?where.email=ada@example.com"));
        var ada = customers.GetProperty("items")[0].GetProperty("id").GetString()!;

        // Invalid data is refused with every problem.
        var bad = await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/records/Ticket", new { status = "lost", colour = "red" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var problems = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(problems, p => p!.Contains("'colour' isn't a field"));
        Assert.Contains(problems, p => p!.Contains("'subject' is required"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/records/Ticket", new { subject = "x", customer = "rec-nope" })).StatusCode);

        // A ticket from the backend: the record trigger starts a run whose action stage marks it triaged.
        using var create = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/solutions/{installId}/records/Ticket") { Content = JsonContent.Create(new { subject = "Can't log in", customer = ada }) };
        create.Headers.Add("Idempotency-Key", "ticket-1");
        var ticket = await Json(await api.SendAsync(create));
        var ticketId = ticket.GetProperty("id").GetString()!;
        Assert.Equal("open", ticket.GetProperty("status").GetString());

        // The same Idempotency-Key returns the same record, not a second one.
        using var again = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/solutions/{installId}/records/Ticket") { Content = JsonContent.Create(new { subject = "Can't log in", customer = ada }) };
        again.Headers.Add("Idempotency-Key", "ticket-1");
        Assert.Equal(ticketId, (await Json(await api.SendAsync(again))).GetProperty("id").GetString());

        var triaged = await EventuallyAsync(async () =>
        {
            var t = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Ticket/{ticketId}"));
            return t.GetProperty("status").GetString() == "triaged" ? (object)t : null;
        }, TimeSpan.FromSeconds(60), "the record trigger's run to triage the ticket");
        Assert.Equal(2, ((JsonElement)triaged).GetProperty("version").GetInt32());

        // The builder's backend got a signed record.created for the ticket (not for the seed data,
        // nor for the update, which this webhook doesn't subscribe to).
        var hook = await EventuallyAsync(() =>
        {
            lock (received) return Task.FromResult(received.FirstOrDefault(r => r.Body.Contains(ticketId)) is { Body: not null } r ? (object)r : null);
        }, TimeSpan.FromSeconds(30), "the webhook");
        var (body, signature) = ((string, string))hook;
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(expected, signature);
        Assert.Equal("record.created", JsonNode.Parse(body)!["event"]!.GetValue<string>());
        lock (received)
        {
            Assert.DoesNotContain(received, r => r.Body.Contains("Old issue"));
            Assert.DoesNotContain(received, r => r.Body.Contains("record.updated"));
        }

        // Optimistic concurrency: a stale version is refused.
        using var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/solutions/{installId}/records/Ticket/{ticketId}") { Content = JsonContent.Create(new { status = "closed" }) };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        Assert.Equal(HttpStatusCode.Conflict, (await api.SendAsync(stale)).StatusCode);
        // A customer with tickets can't be deleted out from under them.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.DeleteAsync($"/api/v1/solutions/{installId}/records/Customer/{ada}")).StatusCode);

        // Evals run for real: one passes, one reports why it failed.
        var evals = await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/evals", new { }));
        output.WriteLine(evals.ToString());
        Assert.Equal(1, evals.GetProperty("passed").GetInt32());
        var failed = evals.GetProperty("results").EnumerateArray().Single(r => !r.GetProperty("passed").GetBoolean());
        Assert.Contains("at least 5 Ticket", failed.GetProperty("failures")[0].GetString());

        // Upgrades: the same version is refused unless forced; a dry run changes nothing; a real one
        // gives the pipeline a new version only where the package changed it, and archives a removed workspace.
        Assert.Equal(HttpStatusCode.Conflict, (await api.PutAsJsonAsync($"/api/v1/solutions/{installId}", HelpdeskPackage("1.0.0", webhookPort: port))).StatusCode);
        var desk = installed.GetProperty("workspaces").EnumerateArray().Single(w => w.GetProperty("key").GetString() == "desk").GetProperty("workspace_id").GetString()!;
        var intake = installed.GetProperty("workspaces").EnumerateArray().Single(w => w.GetProperty("key").GetString() == "intake").GetProperty("workspace_id").GetString()!;
        var deskPipeline = (await Json(await api.GetAsync($"/api/workspaces/{desk}"))).GetProperty("pipeline").GetProperty("version").GetInt32();

        var dry = await Json(await api.PutAsJsonAsync($"/api/v1/solutions/{installId}", new
        {
            bundle = HelpdeskBundle("1.1.0", "Mark it triaged, quickly.", port, withIntake: false),
            dry_run = true
        }));
        Assert.True(dry.GetProperty("dry_run").GetBoolean());
        Assert.Equal("intake", dry.GetProperty("plan").GetProperty("removed_workspaces")[0].GetString());
        Assert.Equal("1.0.0", (await Json(await api.GetAsync($"/api/v1/solutions/{installId}"))).GetProperty("version").GetString());

        var upgraded = await Json(await api.PutAsJsonAsync($"/api/v1/solutions/{installId}", HelpdeskPackage("1.1.0", "Mark it triaged, quickly.", port, withIntake: false)));
        output.WriteLine(upgraded.ToString());
        var deskReport = upgraded.GetProperty("workspaces").EnumerateArray().Single(w => w.GetProperty("key").GetString() == "desk");
        Assert.Contains(deskReport.GetProperty("changes").EnumerateArray(), c => c.GetString() == "trigger 'New ticket' replaced");
        Assert.Equal(deskPipeline, (await Json(await api.GetAsync($"/api/workspaces/{desk}"))).GetProperty("pipeline").GetProperty("version").GetInt32());
        Assert.Equal("Archived", (await Json(await api.GetAsync($"/api/workspaces/{intake}"))).GetProperty("status").GetString());
        var after = await Json(await api.GetAsync($"/api/v1/solutions/{installId}"));
        Assert.Equal("1.1.0", after.GetProperty("version").GetString());
        Assert.Equal(2, after.GetProperty("history").GetArrayLength());

        // The replaced trigger still fires.
        var second = await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/records/Ticket", new { subject = "Second" }));
        await EventuallyAsync(async () =>
            (await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Ticket/{second.GetProperty("id").GetString()}"))).GetProperty("status").GetString() == "triaged"
                ? "ok" : null, TimeSpan.FromSeconds(60), "the upgraded trigger");

        // Uninstall archives its workspaces; purge removes its records.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/solutions/{installId}?purge=true")).StatusCode);
        Assert.Equal("Archived", (await Json(await api.GetAsync($"/api/workspaces/{desk}"))).GetProperty("status").GetString());
        Assert.Empty((await Json(await api.GetAsync("/api/v1/solutions"))).EnumerateArray());
        hooks.Stop();
    }

    [Fact]
    public async Task A_record_trigger_does_not_feed_itself_with_its_own_runs_changes()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Loops", Tenancy.TenantRole.Admin));
        // Fires on every update, and its run updates the record with a value that changes each
        // time: without the guard, each run would start the next one.
        var manifest = """
            name: loopy
            version: 1.0.0
            entities:
              Item:
                fields:
                  name: { type: string, required: true }
                  stamp: { type: string }
            workspaces:
              - key: stamper
                name: Stamper
                goal: Stamp items
                data: { Item: write }
                pipeline:
                  stages:
                    - stage_id: stamp
                      name: Stamp
                      kind: Action
                      tool: data__update_item
                      arguments: { id: "{{ input.record_id }}", fields: { stamp: "run {{ run.number }}" } }
                      retries: 0
                triggers:
                  - { kind: Record, name: Any change, entity: Item, on: [created, updated] }
            """;
        var installId = (await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = new SolutionBundle { Manifest = manifest } })))
            .GetProperty("install_id").GetString()!;
        var item = await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/records/Item", new { name = "widget" }));
        var id = item.GetProperty("id").GetString()!;

        await EventuallyAsync(async () =>
            (await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Item/{id}"))).TryGetProperty("stamp", out _) ? "stamped" : null,
            TimeSpan.FromSeconds(60), "the first run to stamp the item");
        await Task.Delay(TimeSpan.FromSeconds(6));
        var after = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Item/{id}"));
        Assert.Equal(2, after.GetProperty("version").GetInt32());
        Assert.Equal("run 1", after.GetProperty("stamp").GetString());

        // A change from outside (the backend) still fires it.
        await Json(await api.PatchAsJsonAsync($"/api/v1/solutions/{installId}/records/Item/{id}", new { name = "widget v2" }));
        await EventuallyAsync(async () =>
            (await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Item/{id}"))).GetProperty("stamp").GetString() == "run 2" ? "ok" : null,
            TimeSpan.FromSeconds(60), "the trigger to fire for an outside change");
    }

    [Fact]
    public async Task The_helpdesk_sample_runs_end_to_end_and_its_webhook_survives_an_upgrade()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Support", Tenancy.TenantRole.Admin));
        var dir = Path.Combine(RepoRoot(), "samples", "solutions", "helpdesk");
        var bundle = SolutionPackage.FromDirectory(dir);
        var installed = await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle }));
        var installId = installed.GetProperty("install_id").GetString()!;
        foreach (var w in installed.GetProperty("workspaces").EnumerateArray())
        {
            Assert.All(w.GetProperty("triggers").EnumerateArray(), t => Assert.True(t.GetProperty("ok").GetBoolean(), t.ToString()));
        }

        var hook = installed.GetProperty("workspaces").EnumerateArray().Single(w => w.GetProperty("key").GetString() == "intake")
            .GetProperty("triggers")[0].GetProperty("webhook_path").GetString()!;

        // The contact form posts; intake opens the ticket (no model); triage's record trigger
        // classifies it and hands it over for review.
        using var anonymous = Host.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync(hook, new { subject = "Charged twice", message = "Please refund one of the two charges." })).StatusCode);
        var ticket = (JsonElement)await EventuallyAsync(async () =>
        {
            var list = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Ticket?where.subject=Charged twice"));
            return list.GetProperty("total").GetInt32() == 1 && list.GetProperty("items")[0].GetProperty("status").GetString() == "awaiting_review"
                ? (object)list.GetProperty("items")[0] : null;
        }, TimeSpan.FromSeconds(90), "the ticket to be opened and handed over for review");
        Assert.Equal("web", ticket.GetProperty("channel").GetString());
        Assert.True(ticket.TryGetProperty("opened_at", out _));

        // Filters and sorting run in Postgres over every record.
        for (var i = 1; i <= 3; i++)
        {
            await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/records/Ticket",
                new { subject = $"Question {i}", priority = i == 3 ? "urgent" : "low", opened_at = $"2026-10-0{i}T10:00:00Z" }));
        }

        var found = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Ticket?filter=subject~question&filter=opened_at<=2026-10-02T23:59:59Z&order_by=subject&desc=false"));
        Assert.Equal(["Question 1", "Question 2"], found.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("subject").GetString()));
        var notLow = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Ticket?filter=priority!=low&filter=subject~question"));
        Assert.Equal("Question 3", Assert.Single(notLow.GetProperty("items").EnumerateArray()).GetProperty("subject").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync($"/api/v1/solutions/{installId}/records/Ticket?filter=password==x")).StatusCode);

        // Its eval passes.
        var evals = await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/evals", new { }));
        Assert.Equal(0, evals.GetProperty("failed").GetInt32());

        // An upgrade that rewords the webhook's instruction updates it in place: same URL.
        var files = new Dictionary<string, string>(bundle.Files)
        {
            ["workspaces/intake.yaml"] = bundle.Files["workspaces/intake.yaml"].Replace("Open a ticket for this contact-form submission.", "Open a ticket for this submission.")
        };
        var upgraded = await Json(await api.PutAsJsonAsync($"/api/v1/solutions/{installId}",
            new { bundle = new SolutionBundle { Manifest = bundle.Manifest.Replace("version: 1.0.0", "version: 1.0.1"), Files = files } }));
        var intake = upgraded.GetProperty("workspaces").EnumerateArray().Single(w => w.GetProperty("key").GetString() == "intake");
        Assert.Contains(intake.GetProperty("changes").EnumerateArray(), c => c.GetString() == "webhook 'Contact form' updated");
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync(hook, new { subject = "Still works", message = "Same URL." })).StatusCode);
        var intakeTriggers = await Json(await api.GetAsync($"/api/workspaces/{intake.GetProperty("workspace_id").GetString()}/triggers"));
        Assert.Equal("Open a ticket for this submission.", intakeTriggers[0].GetProperty("instruction").GetString());

        // Uninstalled (records kept): they can be read, not changed.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/solutions/{installId}")).StatusCode);
        Assert.True((await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Ticket"))).GetProperty("total").GetInt32() >= 4);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/records/Ticket", new { subject = "late" })).StatusCode);
    }

    [Fact]
    public async Task The_apps_api_honours_allowed_origins_and_rate_limits()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Origins", Tenancy.TenantRole.Admin));
        var manifest = """
            name: origins
            version: 1.0.0
            assistant:
              instructions: Be helpful.
              allowed_origins: ["https://app.example.com"]
              max_messages_per_minute: 2
            """;
        var installId = (await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = new SolutionBundle { Manifest = manifest } })))
            .GetProperty("install_id").GetString()!;
        var token = (await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/end-user-tokens", new { user_id = "u1" }))).GetProperty("token").GetString()!;
        using var client = Host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        async Task<HttpStatusCode> Get(string? origin)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/apps/{installId}");
            if (origin is not null) req.Headers.Add("Origin", origin);
            return (await client.SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Get("https://app.example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("https://evil.example.com"));
        Assert.Equal(HttpStatusCode.OK, await Get(null)); // a backend, not a page

        // Two messages a minute for this user, wherever the requests land.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "one" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "two" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "three" })).StatusCode);
    }

    [Fact]
    public async Task An_assistant_only_connector_gets_its_secrets_from_the_vault()
    {
        if (Skip()) return;
        // A remote plugin the assistant uses, that no workspace has: its secret is given at install.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var service = new HttpListener();
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

        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("AssistantOnly", Tenancy.TenantRole.Admin));
        var manifest = $$"""
            name: crm-helper
            version: 1.0.0
            connectors:
              - name: crm
                plugin: remote-plugin
                settings: { base_url: "http://127.0.0.1:{{port}}" }
            assistant:
              instructions: Look customers up.
              connectors: [crm]
            """;
        var package = new SolutionBundle { Manifest = manifest };
        // Required secrets are asked for up front.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = package })).StatusCode);
        var installId = (await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = package, secrets = new { crm = new { token = "s3cret" } } })))
            .GetProperty("install_id").GetString()!;
        // ...and not again on an upgrade.
        await Json(await api.PutAsJsonAsync($"/api/v1/solutions/{installId}", new { bundle = new SolutionBundle { Manifest = manifest.Replace("1.0.0", "1.0.1") } }));

        var token = (await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/end-user-tokens", new { user_id = "u1" }))).GetProperty("token").GetString()!;
        using var client = Host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var conversation = (await Json(await client.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "Who is ada@example.com?" })))
            .GetProperty("conversation_id").GetString()!;

        var connections = await Json(await api.GetAsync($"/api/tasks/{conversation}/connections"));
        var crm = connections.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "crm");
        Assert.Contains("token", crm.GetProperty("secret_keys").EnumerateArray().Select(k => k.GetString()));
        lock (auths) Assert.Contains("/tools/list Bearer s3cret", auths);
        service.Stop();
    }

    [Fact]
    public async Task End_users_reach_only_their_own_conversations_and_records()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("EndUsers", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);
        var installId = (await Json(await api.PostAsJsonAsync("/api/v1/solutions", HelpdeskPackage("1.0.0")))).GetProperty("install_id").GetString()!;

        async Task<HttpClient> AsEndUser(string user)
        {
            var token = (await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/end-user-tokens", new { user_id = user, name = user, ttl_minutes = 30 })))
                .GetProperty("token").GetString()!;
            var client = Host.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        using var ada = await AsEndUser("user-ada");
        using var bob = await AsEndUser("user-bob");
        using var anonymous = Host.Factory.CreateClient();

        var app = await Json(await ada.GetAsync($"/api/v1/apps/{installId}"));
        Assert.Equal("Help bot", app.GetProperty("assistant").GetString());
        Assert.Equal("How can I help?", app.GetProperty("greeting").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/apps/{installId}")).StatusCode);

        // An end-user token is nothing to the rest of the API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ada.GetAsync("/api/v1/solutions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ada.GetAsync("/api/workspaces")).StatusCode);
        // Nor does it work for another app.
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.GetAsync("/api/v1/apps/sol-other")).StatusCode);

        // Each sees their own records only (the owner field isn't shown to them).
        var adaTickets = await Json(await ada.GetAsync($"/api/v1/apps/{installId}/records/Ticket"));
        Assert.Equal(1, adaTickets.GetProperty("total").GetInt32());
        Assert.False(adaTickets.GetProperty("items")[0].TryGetProperty("requester", out _));
        Assert.Equal(0, (await Json(await bob.GetAsync($"/api/v1/apps/{installId}/records/Ticket"))).GetProperty("total").GetInt32());

        // A conversation: the end user sees their own message, never the assistant's instructions.
        var started = await Json(await ada.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "My printer is on fire" }));
        var conversation = started.GetProperty("conversation_id").GetString()!;
        var view = await Json(await ada.GetAsync($"/api/v1/apps/{installId}/conversations/{conversation}"));
        Assert.Equal("My printer is on fire", view.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("Help customers with their tickets", view.ToString());

        var done = await EventuallyAsync(async () =>
        {
            var v = await Json(await ada.GetAsync($"/api/v1/apps/{installId}/conversations/{conversation}"));
            return v.GetProperty("answering").GetBoolean() ? null : (object)v;
        }, TimeSpan.FromSeconds(90), "the assistant's reply");
        output.WriteLine(done.ToString());
        Assert.Contains(((JsonElement)done).GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "assistant");

        Assert.Single((await Json(await ada.GetAsync($"/api/v1/apps/{installId}/conversations"))).EnumerateArray());
        Assert.Empty((await Json(await bob.GetAsync($"/api/v1/apps/{installId}/conversations"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/apps/{installId}/conversations/{conversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations/{conversation}/messages", new { message = "hi" })).StatusCode);

        // The builder sees the end user's conversation as an ordinary task of the organization.
        var task = await Json(await api.GetAsync($"/api/tasks/{conversation}"));
        Assert.Equal(conversation, task.GetProperty("task_id").GetString());

        // The records connection an end user's conversation gets is bound to them: through it,
        // the assistant's tools see and change that end user's records only.
        var plugin = Host.Factory.Services.GetServices<AgentRuntime.Plugins.IAgentPlugin>().OfType<RecordsPlugin>().Single();
        var tokens = Host.Factory.Services.GetRequiredService<SolutionTokens>();
        var binding = tokens.SignBinding(new RecordsBinding(org.TenantId, installId, "user-bob", new() { ["Ticket"] = "write", ["Customer"] = "read" }));
        var connection = new AgentRuntime.Plugins.PluginConnection
        {
            ConnectionId = "c", WorkspaceId = conversation, Name = "data", Settings = new Dictionary<string, string>(),
            Secrets = new Dictionary<string, string> { [RecordsPlugin.BindingKey] = binding }
        };
        async Task<JsonNode> Call(string tool, object args, string? tenant = null, string key = "")
        {
            var result = await plugin.ExecuteToolAsync(connection, tool, new AgentRuntime.Tools.ToolExecutionRequest
            {
                ToolName = "data__" + tool, AgentId = "agent-1", TaskId = conversation, TenantId = tenant ?? org.TenantId,
                ArgumentsJson = JsonSerializer.Serialize(args), IdempotencyKey = key
            });
            return result.Success ? JsonNode.Parse(result.ResultJson)! : new JsonObject { ["error"] = result.ErrorMessage, ["code"] = result.ErrorCode };
        }

        Assert.Equal(0, (await Call("list_ticket", new { }))["total"]!.GetValue<int>());
        var bobCustomers = await Call("list_customer", new { });
        Assert.Equal(1, bobCustomers["total"]!.GetValue<int>());
        var adaTicket = adaTickets.GetProperty("items")[0].GetProperty("id").GetString()!;
        Assert.Equal("not_found", (await Call("get_ticket", new { id = adaTicket }))["code"]!.GetValue<string>());
        Assert.Equal("not_found", (await Call("update_ticket", new { id = adaTicket, fields = new { status = "closed" } }))["code"]!.GetValue<string>());
        // Linking to someone else's customer record is refused too.
        var adaCustomer = (await Json(await api.GetAsync($"/api/v1/solutions/{installId}/records/Customer?where.email=ada@example.com"))).GetProperty("items")[0].GetProperty("id").GetString();
        Assert.Contains("no Customer", (await Call("create_ticket", new { fields = new { subject = "x", customer = adaCustomer } }))["error"]!.GetValue<string>());
        // What it creates is Bob's, and a replayed create (same idempotency key) makes one record.
        var made = await Call("create_ticket", new { fields = new { subject = "Bob's issue" } }, key: "agent-1:call-1");
        var replay = await Call("create_ticket", new { fields = new { subject = "Bob's issue" } }, key: "agent-1:call-1");
        Assert.Equal(made["created"]!["id"]!.GetValue<string>(), replay["created"]!["id"]!.GetValue<string>());
        Assert.Equal(1, (await Json(await bob.GetAsync($"/api/v1/apps/{installId}/records/Ticket"))).GetProperty("total").GetInt32());
        // Customer is read-only through this connection, and another organization's request is refused.
        Assert.Equal("forbidden", (await Call("update_customer", new { id = "x", fields = new { plan = "pro" } }))["code"]!.GetValue<string>());
        Assert.Equal("forbidden", (await Call("list_ticket", new { }, tenant: "t-someone-else"))["code"]!.GetValue<string>());
    }
}
