using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Tenancy;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>The organization's safety policy over the API: anyone in the organization can read it,
/// only admins change it, every change is audited, and another organization never sees it.</summary>
public sealed class OrganizationPolicyApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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

    private static readonly object Policy = new
    {
        minimum_autonomy = "SemiAutonomous",
        rules = new[] { new { id = "r1", name = "No shell", tool_pattern = "shell_exec", applies = "Any", decision = "Deny" } },
        team = new { max_agents = 8 }
    };

    [Fact]
    public async Task Admins_set_it_members_read_it_changes_are_audited_and_other_organizations_dont_see_it()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("OrgPolicy", TenantRole.Admin);
        using var admin = Host.ClientFor(org);

        var empty = await Json(await admin.GetAsync("/api/organization/policy"));
        Assert.Equal("Autonomous", empty.GetProperty("minimum_autonomy").GetString());

        var saved = await Json(await admin.PutAsJsonAsync("/api/organization/policy", Policy));
        Assert.Equal("SemiAutonomous", saved.GetProperty("minimum_autonomy").GetString());
        Assert.Equal("shell_exec", saved.GetProperty("rules")[0].GetProperty("tool_pattern").GetString());
        Assert.Equal(8, saved.GetProperty("team").GetProperty("max_agents").GetInt32());
        Assert.True(saved.TryGetProperty("updated_at", out var at) && at.ValueKind == JsonValueKind.String);

        var audit = await Json(await admin.GetAsync("/api/organization/policy/audit"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "policy.updated" &&
                                                    e.GetProperty("summary").GetString()!.Contains("SemiAutonomous"));

        // A rule without a pattern is refused.
        var bad = await admin.PutAsJsonAsync("/api/organization/policy", new { rules = new[] { new { tool_pattern = " ", applies = "Any", decision = "Deny" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // A member of the same organization can read it but not change it.
        var memberKey = (await Json(await admin.PostAsJsonAsync("/api/api-keys", new { name = "member", role = "Member" }))).GetProperty("key").GetString()!;
        using var member = Host.ClientFor(org with { ApiKey = memberKey });
        Assert.Equal("SemiAutonomous", (await Json(await member.GetAsync("/api/organization/policy"))).GetProperty("minimum_autonomy").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/organization/policy", new { minimum_autonomy = "Autonomous" })).StatusCode);

        // Another organization has its own (empty) policy.
        using var stranger = Host.ClientFor(await Host.CreateOrganizationAsync("OrgPolicyStranger", TenantRole.Admin));
        Assert.Equal("Autonomous", (await Json(await stranger.GetAsync("/api/organization/policy"))).GetProperty("minimum_autonomy").GetString());
        Assert.Empty((await Json(await stranger.GetAsync("/api/organization/policy/audit"))).EnumerateArray());
    }
}
