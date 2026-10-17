using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Solutions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// The parts of the platform builders ship with (docs/solutions.md): the registry, usage for
/// billing, per-field indexes, and end users signing in with the builder's own tokens.
/// </summary>
public sealed class SolutionPlatformApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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

    private static string Package(string name, string version, string extra = "") => $"""
        name: {name}
        title: {name} app
        version: {version}
        description: A package for the registry tests.
        entities:
          Order:
            fields:
              total: {"{"} type: number, required: true, indexed: true {"}"}
              status: {"{"} type: enum, values: [new, paid], default: new, indexed: true {"}"}
        workspaces:
          - key: orders
            name: Orders
            goal: Handle orders
            data: {"{"} Order: write {"}"}
        {extra}
        """;

    [Fact]
    public async Task Packages_are_published_found_installed_by_name_upgraded_and_yanked()
    {
        if (Skip()) return;
        var publisherOrg = await Host.CreateOrganizationAsync("Publisher", Tenancy.TenantRole.Admin);
        using var publisher = Host.ClientFor(publisherOrg);
        using var customer = Host.ClientFor(await Host.CreateOrganizationAsync("Customer", Tenancy.TenantRole.Admin));
        using var squatter = Host.ClientFor(await Host.CreateOrganizationAsync("Squatter", Tenancy.TenantRole.Admin));
        var name = "orders-" + Guid.NewGuid().ToString("n")[..6];

        // Private by default: the customer can't see it.
        var v1 = await Json(await publisher.PostAsJsonAsync("/api/v1/registry/packages", new { bundle = new SolutionBundle { Manifest = Package(name, "1.0.0") } }));
        Assert.Equal("organization", v1.GetProperty("visibility").GetString());
        Assert.Equal(64, v1.GetProperty("sha256").GetString()!.Length);
        Assert.DoesNotContain((await Json(await customer.GetAsync("/api/v1/registry/packages"))).EnumerateArray(), p => p.GetProperty("name").GetString() == name);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync("/api/v1/solutions", new { package = name })).StatusCode);

        // Public from 1.1.0; versions are immutable and only go up; the name is the publisher's.
        await Json(await publisher.PostAsJsonAsync("/api/v1/registry/packages", new { bundle = new SolutionBundle { Manifest = Package(name, "1.1.0") }, visibility = "public" }));
        Assert.Equal(HttpStatusCode.Conflict, (await publisher.PostAsJsonAsync("/api/v1/registry/packages", new { bundle = new SolutionBundle { Manifest = Package(name, "1.1.0") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await publisher.PostAsJsonAsync("/api/v1/registry/packages", new { bundle = new SolutionBundle { Manifest = Package(name, "0.9.0") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await squatter.PostAsJsonAsync("/api/v1/registry/packages", new { bundle = new SolutionBundle { Manifest = Package(name, "9.0.0") }, visibility = "public" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await publisher.PostAsJsonAsync("/api/v1/registry/packages", new { bundle = new SolutionBundle { Manifest = "name: x" } })).StatusCode);

        var found = (await Json(await customer.GetAsync($"/api/v1/registry/packages?q={name}"))).EnumerateArray().Single();
        Assert.Equal("1.1.0", found.GetProperty("version").GetString());
        Assert.Equal("Publisher", found.GetProperty("publisher").GetString());

        // Installed by name (latest), with its indexes; the install is counted.
        var installed = await Json(await customer.PostAsJsonAsync("/api/v1/solutions", new { package = name }));
        Assert.Equal($"{name}@1.1.0", installed.GetProperty("package").GetString());
        var installId = installed.GetProperty("install_id").GetString()!;
        Assert.Equal(1, (await Json(await customer.GetAsync($"/api/v1/registry/packages?q={name}")))[0].GetProperty("installs").GetInt32());

        await using (var db = Host.Factory.Services.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContext())
        {
            var indexes = await db.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'SolutionRecords'").ToListAsync();
            Assert.Contains(RecordQuerySql.IndexName(installId, "Order", "total"), indexes);
            Assert.Contains(RecordQuerySql.IndexName(installId, "Order", "status"), indexes);
        }

        // Filtering on an indexed field uses the index (the expression matches exactly).
        for (var i = 0; i < 5; i++) await Json(await customer.PostAsJsonAsync($"/api/v1/solutions/{installId}/records/Order", new { total = 10 * i }));
        var big = await Json(await customer.GetAsync($"/api/v1/solutions/{installId}/records/Order?filter=total>=20&order_by=total&desc=false"));
        Assert.Equal([20m, 30m, 40m], big.GetProperty("items").EnumerateArray().Select(o => o.GetProperty("total").GetDecimal()));
        await using (var conn = new NpgsqlConnection(Host.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var off = new NpgsqlCommand("SET enable_seqscan = off", conn)) await off.ExecuteNonQueryAsync();
            await using var explain = new NpgsqlCommand(
                $"""EXPLAIN SELECT * FROM "SolutionRecords" WHERE "InstallId" = '{installId}' AND "Entity" = 'Order' AND (CASE WHEN jsonb_typeof("DataJson" -> 'total') = 'number' THEN ("DataJson" ->> 'total')::numeric END) >= 20""", conn);
            var plan = new StringBuilder();
            await using (var reader = await explain.ExecuteReaderAsync()) while (await reader.ReadAsync()) plan.AppendLine(reader.GetString(0));
            Assert.Contains(RecordQuerySql.IndexName(installId, "Order", "total"), plan.ToString());
        }

        // An upgrade to a newer published version, by name; a version that stops indexing a field drops its index.
        await Json(await publisher.PostAsJsonAsync("/api/v1/registry/packages",
            new { bundle = new SolutionBundle { Manifest = Package(name, "1.2.0").Replace("type: number, required: true, indexed: true", "type: number, required: true") }, visibility = "public" }));
        var upgraded = await Json(await customer.PutAsJsonAsync($"/api/v1/solutions/{installId}", new { package = $"{name}@1.2.0" }));
        Assert.Equal("1.2.0", upgraded.GetProperty("version").GetString());
        await using (var db = Host.Factory.Services.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContext())
        {
            var indexes = await db.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'SolutionRecords'").ToListAsync();
            Assert.DoesNotContain(RecordQuerySql.IndexName(installId, "Order", "total"), indexes);
            Assert.Contains(RecordQuerySql.IndexName(installId, "Order", "status"), indexes);
        }

        // Yanked: "latest" skips it; an exact version still resolves; only the publisher yanks.
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync($"/api/v1/registry/packages/{name}/1.2.0/yank", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await publisher.PostAsJsonAsync($"/api/v1/registry/packages/{name}/1.2.0/yank", new { reason = "broken migration" })).StatusCode);
        Assert.Equal("1.1.0", (await Json(await customer.GetAsync($"/api/v1/registry/packages/{name}/latest"))).GetProperty("version").GetString());
        Assert.True((await Json(await customer.GetAsync($"/api/v1/registry/packages/{name}/1.2.0"))).GetProperty("yanked").GetBoolean());

        // A version altered in the database after publishing fails its integrity check.
        await using (var db = Host.Factory.Services.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContext())
        {
            await db.SolutionPackages.Where(p => p.PackageId == $"{name}@1.1.0")
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.BundleJson, p => p.BundleJson.Replace("Handle orders", "Exfiltrate orders")));
        }

        var tampered = await customer.GetAsync($"/api/v1/registry/packages/{name}/1.1.0");
        Assert.Equal(HttpStatusCode.Conflict, tampered.StatusCode);
        Assert.Contains("integrity", await tampered.Content.ReadAsStringAsync());

        // Publishing what's installed, from its page.
        using var own = Host.ClientFor(await Host.CreateOrganizationAsync("SelfPublisher", Tenancy.TenantRole.Admin));
        var ownName = "mine-" + Guid.NewGuid().ToString("n")[..6];
        var ownInstall = (await Json(await own.PostAsJsonAsync("/api/v1/solutions", new { bundle = new SolutionBundle { Manifest = Package(ownName, "2.0.0") } })))
            .GetProperty("install_id").GetString();
        var published = await Json(await own.PostAsJsonAsync($"/api/v1/solutions/{ownInstall}/publish", new { visibility = "organization" }));
        Assert.Equal($"{ownName}@2.0.0", published.GetProperty("package").GetString());
    }

    [Fact]
    public async Task Usage_is_reported_per_end_user_and_a_monthly_cap_stops_them()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Billing", Tenancy.TenantRole.Admin));
        var manifest = """
            name: billed
            version: 1.0.0
            assistant:
              instructions: Be helpful.
              monthly_cost_limit_usd_per_user: 0.000001
            """;
        var installId = (await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = new SolutionBundle { Manifest = manifest } })))
            .GetProperty("install_id").GetString()!;

        async Task<HttpClient> As(string user)
        {
            var token = (await Json(await api.PostAsJsonAsync($"/api/v1/solutions/{installId}/end-user-tokens", new { user_id = user }))).GetProperty("token").GetString()!;
            var c = Host.Factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }

        using var ada = await As("ada");
        var conversation = (await Json(await ada.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "Hello" }))).GetProperty("conversation_id").GetString()!;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && (await Json(await ada.GetAsync($"/api/v1/apps/{installId}/conversations/{conversation}"))).GetProperty("answering").GetBoolean())
        {
            await Task.Delay(500);
        }

        var usage = await Json(await api.GetAsync($"/api/v1/solutions/{installId}/usage?by=end_user"));
        output.WriteLine(usage.ToString());
        var line = usage.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("ada", line.GetProperty("key").GetString());
        Assert.Equal(1, line.GetProperty("conversations").GetInt32());
        Assert.True(line.GetProperty("tokens").GetInt64() > 0);
        Assert.True(usage.GetProperty("cost_usd").GetDecimal() > 0);

        var csv = await api.GetAsync($"/api/v1/solutions/{installId}/usage?by=day&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("day,conversations,runs,tokens,cost_usd", await csv.Content.ReadAsStringAsync());

        // Ada spent more than her cap this month: no more conversations or messages until next month.
        Assert.Equal(HttpStatusCode.PaymentRequired, (await ada.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await ada.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations/{conversation}/messages", new { message = "More" })).StatusCode);
        // Bob has his own cap.
        using var bob = await As("bob");
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/v1/apps/{installId}/conversations", new { message = "Hi" })).StatusCode);
    }

    [Fact]
    public async Task End_users_sign_in_with_the_builders_own_tokens()
    {
        if (Skip()) return;
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwks = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["kid"] = "k1", ["n"] = B64(p.Modulus!), ["e"] = B64(p.Exponent!) }) }.ToJsonString();

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var idp = new HttpListener();
        idp.Prefixes.Add($"http://127.0.0.1:{port}/");
        idp.Start();
        _ = Task.Run(async () =>
        {
            while (idp.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await idp.GetContextAsync(); } catch { return; }
                var bytes = Encoding.UTF8.GetBytes(jwks);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("Federated", Tenancy.TenantRole.Admin));
        var manifest = $$$"""
            name: federated
            version: 1.0.0
            inputs:
              - { key: issuer, default: "https://auth.example.com/" }
            entities:
              Note:
                fields:
                  text: { type: string, required: true }
                  owner: { type: end_user }
            assistant:
              instructions: Help.
              data: { Note: read }
            end_users:
              jwt:
                issuer: "{{ inputs.issuer }}"
                audience: notes-app
                jwks_url: http://127.0.0.1:{{{port}}}/.well-known/jwks.json
            seed:
              Note:
                - { text: "Sam's note", owner: user-sam }
                - { text: "Someone else's", owner: user-other }
            """;
        var installId = (await Json(await api.PostAsJsonAsync("/api/v1/solutions", new { bundle = new SolutionBundle { Manifest = manifest } })))
            .GetProperty("install_id").GetString()!;

        string Token(string sub, string aud = "notes-app")
        {
            var h = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "k1", typ = "JWT" }));
            var b = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = "https://auth.example.com/", aud, sub, name = "Sam", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() }));
            return $"{h}.{b}.{B64(rsa.SignData(Encoding.ASCII.GetBytes($"{h}.{b}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }

        using var client = Host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("user-sam"));
        var app = await Json(await client.GetAsync($"/api/v1/apps/{installId}"));
        Assert.Equal("user-sam", app.GetProperty("user").GetProperty("id").GetString());
        var notes = await Json(await client.GetAsync($"/api/v1/apps/{installId}/records/Note"));
        Assert.Equal("Sam's note", Assert.Single(notes.GetProperty("items").EnumerateArray()).GetProperty("text").GetString());

        // A token for another audience, or the same JWT anywhere else in the API, gets nothing.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("user-sam", aud: "other-app"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/apps/{installId}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("user-sam"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/solutions")).StatusCode);
        idp.Stop();
    }
}
