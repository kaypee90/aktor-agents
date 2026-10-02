using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>Organization model settings through the real API and Postgres (docs/llm-settings.md):
/// Admin-only changes, keys never returned, validation, and other organizations unaffected.</summary>
public sealed class LlmSettingsApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Admins_choose_the_model_keys_stay_secret_and_reset_goes_back_to_the_server()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("ModelAdmin", Tenancy.TenantRole.Admin);
        var other = await Host.CreateOrganizationAsync("ModelOther", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);
        using var otherApi = Host.ClientFor(other);

        var initial = await Json(await api.GetAsync("/api/llm/settings"));
        Assert.Equal("server", initial.GetProperty("source").GetString());
        Assert.Equal("Mock", initial.GetProperty("effective").GetProperty("provider").GetString());

        // A cloud provider needs a key (the test server's own provider is Mock, so none to borrow).
        var noKey = await api.PutAsJsonAsync("/api/llm/settings", new { provider = "OpenAI", model = "gpt-5" });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        // Prices typed per million instead of per token are refused.
        var badPrice = await api.PutAsJsonAsync("/api/llm/settings", new { provider = "OpenAI", model = "gpt-5", api_key = "sk-test", price_per_input_token_usd = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, badPrice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsJsonAsync("/api/llm/settings", new { provider = "Nope", model = "x" })).StatusCode);

        var saved = await Json(await api.PutAsJsonAsync("/api/llm/settings", new
        {
            provider = "openai", model = "gpt-5", api_key = "sk-secret-value", price_per_input_token_usd = 0.00000125m, price_per_output_token_usd = 0.00001m
        }));
        Assert.Equal("organization", saved.GetProperty("source").GetString());
        Assert.Equal("OpenAI", saved.GetProperty("effective").GetProperty("provider").GetString());
        Assert.Equal(1.25m, saved.GetProperty("effective").GetProperty("price_per_million_input_usd").GetDecimal());
        Assert.True(saved.GetProperty("organization").GetProperty("api_key_set").GetBoolean());

        // The key is never sent back.
        var raw = await (await api.GetAsync("/api/llm/settings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-secret-value", raw);

        // Saving again without a key keeps the stored one.
        Assert.True((await api.PutAsJsonAsync("/api/llm/settings", new { provider = "OpenAI", model = "gpt-5-mini" })).IsSuccessStatusCode);

        // Other organizations still use the server's model.
        Assert.Equal("server", (await Json(await otherApi.GetAsync("/api/llm/settings"))).GetProperty("source").GetString());

        var reset = await Json(await api.DeleteAsync("/api/llm/settings"));
        Assert.Equal("server", reset.GetProperty("source").GetString());
        // The saved key went with it.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsJsonAsync("/api/llm/settings", new { provider = "OpenAI", model = "gpt-5" })).StatusCode);
    }

    [Fact]
    public async Task The_demo_provider_tests_and_lists_without_a_key_and_members_cannot_change_it()
    {
        if (Skip()) return;
        var admin = Host.ClientFor(await Host.CreateOrganizationAsync("ModelTest", Tenancy.TenantRole.Admin));
        var test = await Json(await admin.PostAsJsonAsync("/api/llm/test", new { provider = "Mock" }));
        Assert.True(test.GetProperty("ok").GetBoolean(), test.ToString());
        var models = await Json(await admin.PostAsJsonAsync("/api/llm/models", new { provider = "Mock" }));
        Assert.Equal("mock", models.GetProperty("models")[0].GetString());
        Assert.Equal(5, (await Json(await admin.GetAsync("/api/llm/providers"))).GetArrayLength());

        var member = Host.ClientFor(await Host.CreateOrganizationAsync("ModelMember", Tenancy.TenantRole.Member));
        Assert.True((await member.GetAsync("/api/llm/settings")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/llm/settings", new { provider = "Mock" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/llm/test", new { provider = "Mock" })).StatusCode);
    }
}
