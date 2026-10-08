using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>Organization models through the real API and Postgres (docs/llm-settings.md): several
/// named models, Admin-only changes, keys never returned, validation, a model per task and a switch
/// mid-run, and other organizations unaffected.</summary>
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
    public async Task Admins_set_up_several_models_keys_stay_secret_and_reset_goes_back_to_the_server()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("ModelAdmin", Tenancy.TenantRole.Admin));
        using var otherApi = Host.ClientFor(await Host.CreateOrganizationAsync("ModelOther", Tenancy.TenantRole.Admin));

        var initial = await Json(await api.GetAsync("/api/llm/settings"));
        Assert.Equal("server", initial.GetProperty("default_profile_id").GetString());
        Assert.Equal(0, initial.GetProperty("profiles").GetArrayLength());

        // A cloud provider needs a key (the test server's own provider is Mock, so none to borrow).
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/llm/profiles", new { name = "GPT", provider = "OpenAI", model = "gpt-5" })).StatusCode);
        // Prices typed per million instead of per token are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/llm/profiles",
            new { name = "GPT", provider = "OpenAI", model = "gpt-5", api_key = "sk-test", price_per_input_token_usd = 3 })).StatusCode);

        var saved = await Json(await api.PostAsJsonAsync("/api/llm/profiles", new
        {
            name = "GPT-5 (best)", provider = "openai", model = "gpt-5", api_key = "sk-secret-value",
            price_per_input_token_usd = 0.00000125m, price_per_output_token_usd = 0.00001m
        }));
        await Json(await api.PostAsJsonAsync("/api/llm/profiles", new { name = "Local Qwen", provider = "Ollama", model = "qwen3:8b" }));
        await Json(await api.PostAsJsonAsync("/api/llm/profiles", new { name = "Demo", provider = "Mock", make_default = true }));
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync("/api/llm/profiles", new { name = "Demo", provider = "Mock" })).StatusCode);

        var view = await Json(await api.GetAsync("/api/llm/settings"));
        Assert.Equal(3, view.GetProperty("profiles").GetArrayLength());
        Assert.Equal("demo", view.GetProperty("default_profile_id").GetString());
        var gpt = view.GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "gpt-5-best");
        Assert.True(gpt.GetProperty("api_key_set").GetBoolean());
        Assert.Equal(1.25m, gpt.GetProperty("price_per_million_input_usd").GetDecimal());
        var qwen = view.GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "local-qwen");
        Assert.Equal(0m, qwen.GetProperty("price_per_million_input_usd").GetDecimal());

        // The key is never sent back.
        Assert.DoesNotContain("sk-secret-value", await (await api.GetAsync("/api/llm/settings")).Content.ReadAsStringAsync());
        // Editing without a key keeps it; moving it to another address needs the key again.
        await Json(await api.PutAsJsonAsync("/api/llm/profiles/gpt-5-best", new { name = "GPT-5 (best)", provider = "OpenAI", model = "gpt-5.1" }));
        // Saved without prices, a listed model is counted at its list price.
        var listed = (await Json(await api.GetAsync("/api/llm/settings"))).GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "gpt-5-best");
        Assert.Equal("list", listed.GetProperty("price_source").GetString());
        Assert.Equal(10m, listed.GetProperty("price_per_million_output_usd").GetDecimal());
        var openAi = (await Json(await api.GetAsync("/api/llm/providers"))).EnumerateArray().Single(p => p.GetProperty("id").GetString() == "OpenAI");
        Assert.Contains(openAi.GetProperty("models").EnumerateArray(), m => m.GetProperty("id").GetString() == "gpt-6-astra"
            && m.GetProperty("input_per_million_usd").GetDecimal() == 10m);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsJsonAsync("/api/llm/profiles/gpt-5-best",
            new { name = "GPT-5 (best)", provider = "OpenAI", model = "gpt-5.1", base_url = "https://elsewhere.example.com" })).StatusCode);

        // Other organizations still use the server's model.
        Assert.Equal("server", (await Json(await otherApi.GetAsync("/api/llm/settings"))).GetProperty("default_profile_id").GetString());

        await Json(await api.PutAsJsonAsync("/api/llm/default", new { profile_id = "server" }));
        Assert.Equal("server", (await Json(await api.GetAsync("/api/llm/settings"))).GetProperty("default_profile_id").GetString());
        Assert.Equal(2, (await Json(await api.DeleteAsync("/api/llm/profiles/local-qwen"))).GetProperty("profiles").GetArrayLength());

        var reset = await Json(await api.DeleteAsync("/api/llm/settings"));
        Assert.Equal(0, reset.GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public async Task A_task_runs_on_the_model_it_picks_and_finished_tasks_fork_instead_of_switching()
    {
        if (Skip()) return;
        using var api = Host.ClientFor(await Host.CreateOrganizationAsync("ModelTasks", Tenancy.TenantRole.Admin));
        await Json(await api.PostAsJsonAsync("/api/llm/profiles", new { id = "demo-a", name = "Demo A", provider = "Mock" }));
        await Json(await api.PostAsJsonAsync("/api/llm/profiles", new { id = "demo-b", name = "Demo B", provider = "Mock" }));

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/tasks", new { goal = "Say hello.", model = "nope" })).StatusCode);

        var started = await Json(await api.PostAsJsonAsync("/api/tasks", new { goal = "Say hello.", model = "demo-b" }));
        var taskId = started.GetProperty("task_id").GetString()!;
        await Json(await api.GetAsync($"/api/tasks/{taskId}/wait?timeout_seconds=60"));
        var task = await Json(await api.GetAsync($"/api/tasks/{taskId}"));
        Assert.Equal("demo-b", task.GetProperty("model").GetProperty("profile_id").GetString());

        // Every call was made with that model, and analytics says so.
        JsonElement byModel = default;
        for (var i = 0; i < 20; i++)
        {
            byModel = (await Json(await api.GetAsync("/api/analytics?range=24h"))).GetProperty("by_model");
            if (byModel.GetArrayLength() > 0) break;
            await Task.Delay(500);
        }

        Assert.All(byModel.EnumerateArray(), m => Assert.Equal("demo-b", m.GetProperty("profile_id").GetString()));
        Assert.True(byModel[0].GetProperty("calls").GetInt32() >= 1);

        // A finished task can't be switched: fork it onto another model instead.
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync($"/api/tasks/{taskId}/model", new { model = "demo-a" })).StatusCode);
        var journal = await Json(await api.GetAsync($"/api/tasks/{taskId}/journal"));
        var firstStep = journal.EnumerateArray().First().GetProperty("seq").GetInt64();
        var fork = await Json(await api.PostAsJsonAsync($"/api/tasks/{taskId}/replay", new { mode = "fork", fork_after_step = firstStep, model = "demo-a" }));
        var forked = await Json(await api.GetAsync($"/api/tasks/{fork.GetProperty("task_id").GetString()}"));
        Assert.Equal("demo-a", forked.GetProperty("model").GetProperty("profile_id").GetString());
    }

    [Fact]
    public async Task The_demo_provider_tests_and_lists_without_a_key_and_members_cannot_change_models()
    {
        if (Skip()) return;
        using var admin = Host.ClientFor(await Host.CreateOrganizationAsync("ModelTest", Tenancy.TenantRole.Admin));
        var test = await Json(await admin.PostAsJsonAsync("/api/llm/test", new { provider = "Mock" }));
        Assert.True(test.GetProperty("ok").GetBoolean(), test.ToString());
        Assert.Equal("mock", (await Json(await admin.PostAsJsonAsync("/api/llm/models", new { provider = "Mock" }))).GetProperty("models")[0].GetString());
        Assert.Equal(5, (await Json(await admin.GetAsync("/api/llm/providers"))).GetArrayLength());

        using var member = Host.ClientFor(await Host.CreateOrganizationAsync("ModelMember", Tenancy.TenantRole.Member));
        Assert.True((await member.GetAsync("/api/llm/settings")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/llm/profiles", new { name = "x", provider = "Mock" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/llm/test", new { provider = "Mock" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/llm/default", new { profile_id = "server" })).StatusCode);
    }
}
