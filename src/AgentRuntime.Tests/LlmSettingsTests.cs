using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.Integrations;
using AgentRuntime.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Tests;

/// <summary>Organization model profiles (docs/llm-settings.md): how they combine with the server's
/// settings, where keys may go, which profile a call uses, and which provider it reaches.</summary>
public sealed class LlmSettingsTests
{
    private static readonly LlmOptions Server = new()
    {
        Provider = "Anthropic",
        Model = "claude-sonnet-5-5",
        ApiKey = "server-key",
        PricePerInputTokenUsd = 0.000003m,
        PricePerOutputTokenUsd = 0.000015m
    };

    private sealed class MemorySecrets : ISecretStore
    {
        public readonly ConcurrentDictionary<(string, string), string> Values = new();
        public Task PutAsync(string scope, string key, string value, CancellationToken ct = default) { Values[(scope, key)] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string scope, string key, CancellationToken ct = default) => Task.FromResult(Values.TryGetValue((scope, key), out var v) ? v : null);
        public Task DeleteScopeAsync(string scope, CancellationToken ct = default)
        {
            foreach (var k in Values.Keys.Where(k => k.Item1 == scope)) Values.TryRemove(k, out _);
            return Task.CompletedTask;
        }
    }

    private static ModelProfile Profile(string id, string provider, string model, string? baseUrl = null) =>
        new() { Id = id, Name = id, Provider = provider, Model = model, BaseUrl = baseUrl };

    [Fact]
    public void The_server_key_is_used_only_for_the_servers_provider_at_the_servers_address()
    {
        var same = LlmSettingsService.Apply(Server, Profile("opus", "Anthropic", "claude-opus-5-5"), apiKey: null);
        Assert.Equal("server-key", same.ApiKey);
        Assert.Equal(("opus", "claude-opus-5-5"), (same.ProfileId, same.Model));

        // An address the organization chose never receives the server's key.
        Assert.Null(LlmSettingsService.Apply(Server, Profile("p", "Anthropic", "m", "https://proxy.example.com"), apiKey: null).ApiKey);
        Assert.Null(LlmSettingsService.Apply(Server, Profile("gpt", "OpenAI", "gpt-5"), apiKey: null).ApiKey);
        Assert.Equal("org-key", LlmSettingsService.Apply(Server, Profile("gpt", "OpenAI", "gpt-5"), apiKey: "org-key").ApiKey);
    }

    [Fact]
    public void Local_models_are_free_unless_priced_and_the_servers_options_are_untouched()
    {
        var local = LlmSettingsService.Apply(Server, Profile("qwen", "Ollama", "qwen3:8b"), null);
        Assert.Equal(0, local.PricePerInputTokenUsd);
        Assert.Equal(0, local.PricePerOutputTokenUsd);

        var priced = LlmSettingsService.Apply(Server, Profile("gpt", "OpenAI", "gpt-5") with
        {
            PricePerInputTokenUsd = 0.00000125m, PricePerOutputTokenUsd = 0.00001m
        }, "k");
        Assert.Equal(0.00000125m, priced.PricePerInputTokenUsd);
        Assert.Equal(0.00001m, priced.PricePerOutputTokenUsd);

        Assert.Equal("Anthropic", Server.Provider);
        Assert.Null(Server.ProfileId);
    }

    [Fact]
    public async Task Calls_use_the_named_profile_else_the_default_else_the_server_and_keys_stay_with_their_profile()
    {
        var service = new LlmSettingsService(new MemorySecrets(), Options.Create(Server));
        await service.SaveProfileAsync("org-a", Profile("cheap", "OpenAI", "gpt-5-mini"), "cheap-key", makeDefault: false);
        await service.SaveProfileAsync("org-a", Profile("router", "OpenAI", "some/model", "https://openrouter.ai/api"), "router-key", makeDefault: false);

        // The first profile became the default; a named one wins; an unknown or deleted one falls back.
        Assert.Equal(("cheap", "cheap-key"), ((await service.ResolveAsync("org-a")).ProfileId, (await service.ResolveAsync("org-a")).ApiKey));
        var router = await service.ResolveAsync("org-a", "router");
        Assert.Equal(("some/model", "router-key"), (router.Model, router.ApiKey));
        Assert.Equal("cheap", (await service.ResolveAsync("org-a", "gone")).ProfileId);
        Assert.Same(Server, await service.ResolveAsync("org-a", ModelProfiles.ServerId));

        Assert.True(await service.SetDefaultAsync("org-a", ModelProfiles.ServerId));
        Assert.Same(Server, await service.ResolveAsync("org-a"));
        Assert.Equal("router-key", (await service.ResolveAsync("org-a", "router")).ApiKey);

        // Other organizations, and calls without one, use the server's configuration.
        Assert.Same(Server, await service.ResolveAsync("org-b", "cheap"));
        Assert.Same(Server, await service.ResolveAsync(null));

        Assert.True(await service.DeleteProfileAsync("org-a", "router"));
        Assert.Null(await service.GetApiKeyAsync("org-a", Profile("router", "OpenAI", "x")));

        await service.ClearAsync("org-a");
        Assert.Same(Server, await service.ResolveAsync("org-a", "cheap"));
    }

    [Fact]
    public async Task Settings_saved_before_profiles_become_one_profile_and_keep_their_key()
    {
        var secrets = new MemorySecrets();
        var json = JsonSerializer.Serialize(new { provider = "OpenAI", model = "gpt-5", updatedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await secrets.PutAsync("llm:org-old", "settings", json);
        await secrets.PutAsync("llm:org-old", "api_key:OpenAI", "old-key");

        var service = new LlmSettingsService(secrets, Options.Create(Server));
        var resolved = await service.ResolveAsync("org-old");
        Assert.Equal(("OpenAI", "gpt-5", "old-key"), (resolved.Provider, resolved.Model, resolved.ApiKey));

        // The key now belongs to that profile only: a new profile for the same provider doesn't get it.
        await service.SaveProfileAsync("org-old", Profile("other", "OpenAI", "gpt-5-mini", "https://elsewhere.example.com"), null, makeDefault: false);
        Assert.Null((await service.ResolveAsync("org-old", "other")).ApiKey);
    }

    [Fact]
    public async Task Calls_reach_the_profiles_provider_or_else_the_servers()
    {
        var serverOptions = Options.Create(new LlmOptions { Provider = "Anthropic", Model = "server-model", ApiKey = "k" });
        var service = new LlmSettingsService(new MemorySecrets(), serverOptions);
        await service.SaveProfileAsync("demo-org", Profile("demo", "Mock", ""), null, makeDefault: true);

        var server = new CountingProvider();
        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var router = new OrganizationLlmRouter(server, service, new LlmProviderFactory(http, serverOptions, new HeuristicMockLlmProvider()));

        var request = new LlmCompletionRequest { Messages = [ChatMessage.System("s"), ChatMessage.User("hello")] };
        await router.CompleteAsync(request with { TenantId = "other-org" });
        await router.CompleteAsync(request);
        await router.CompleteAsync(request with { TenantId = "demo-org", ModelProfileId = ModelProfiles.ServerId });
        Assert.Equal(3, server.Calls);

        // The organization's default is the demo provider: the server's provider isn't called.
        var answer = await router.CompleteAsync(request with { TenantId = "demo-org" });
        Assert.Equal(3, server.Calls);
        Assert.NotEqual(LlmFinishReason.Error, answer.FinishReason);
    }

    [Theory]
    [InlineData("GPT-5 mini (cheap)", "gpt-5-mini-cheap")]
    [InlineData("  Local Qwen  ", "local-qwen")]
    [InlineData("server", "model")]
    [InlineData("!!!", "model")]
    public void Profile_ids_come_from_names(string name, string id)
    {
        Assert.Equal(id, ModelProfiles.Slug(name));
        Assert.True(ModelProfiles.IsValidId(id));
    }

    private sealed class CountingProvider : ILLMProvider
    {
        public int Calls;
        public string ProviderName => "Server";

        public Task<LlmCompletionResponse> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new LlmCompletionResponse { Content = "ok", FinishReason = LlmFinishReason.Stop });
        }
    }
}
