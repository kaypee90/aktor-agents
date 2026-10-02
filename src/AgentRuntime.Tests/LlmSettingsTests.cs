using System.Collections.Concurrent;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.Integrations;
using AgentRuntime.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Tests;

/// <summary>Organization model settings (docs/llm-settings.md): how they combine with the
/// server's, where keys may go, and which provider a call reaches.</summary>
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
        private readonly ConcurrentDictionary<(string, string), string> _values = new();
        public Task PutAsync(string scope, string key, string value, CancellationToken ct = default) { _values[(scope, key)] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string scope, string key, CancellationToken ct = default) => Task.FromResult(_values.TryGetValue((scope, key), out var v) ? v : null);
        public Task DeleteScopeAsync(string scope, CancellationToken ct = default)
        {
            foreach (var k in _values.Keys.Where(k => k.Item1 == scope)) _values.TryRemove(k, out _);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void The_server_key_is_used_only_for_the_servers_provider_at_the_servers_address()
    {
        var same = LlmSettingsService.Apply(Server, new OrganizationLlmSettings { Provider = "Anthropic", Model = "claude-opus-5-5" }, apiKey: null);
        Assert.Equal("server-key", same.ApiKey);
        Assert.Equal("claude-opus-5-5", same.Model);

        // An address the organization chose never receives the server's key.
        var elsewhere = LlmSettingsService.Apply(Server, new OrganizationLlmSettings { Provider = "Anthropic", Model = "m", BaseUrl = "https://proxy.example.com" }, apiKey: null);
        Assert.Null(elsewhere.ApiKey);

        var otherProvider = LlmSettingsService.Apply(Server, new OrganizationLlmSettings { Provider = "OpenAI", Model = "gpt-5" }, apiKey: null);
        Assert.Null(otherProvider.ApiKey);

        var own = LlmSettingsService.Apply(Server, new OrganizationLlmSettings { Provider = "OpenAI", Model = "gpt-5" }, apiKey: "org-key");
        Assert.Equal("org-key", own.ApiKey);
    }

    [Fact]
    public void Local_models_are_free_unless_priced_and_the_servers_options_are_untouched()
    {
        var local = LlmSettingsService.Apply(Server, new OrganizationLlmSettings { Provider = "Ollama", Model = "qwen3:8b" }, null);
        Assert.Equal(0, local.PricePerInputTokenUsd);
        Assert.Equal(0, local.PricePerOutputTokenUsd);

        var priced = LlmSettingsService.Apply(Server, new OrganizationLlmSettings
        {
            Provider = "OpenAI", Model = "gpt-5", PricePerInputTokenUsd = 0.00000125m, PricePerOutputTokenUsd = 0.00001m
        }, "k");
        Assert.Equal(0.00000125m, priced.PricePerInputTokenUsd);
        Assert.Equal(0.00001m, priced.PricePerOutputTokenUsd);

        Assert.Equal("Anthropic", Server.Provider);
        Assert.Equal(0.000003m, Server.PricePerInputTokenUsd);
    }

    [Fact]
    public async Task Saved_settings_apply_to_their_organization_only_and_reset_restores_the_server()
    {
        var service = new LlmSettingsService(new MemorySecrets(), Options.Create(Server));
        await service.SaveAsync("org-a", new OrganizationLlmSettings { Provider = "OpenAI", Model = "gpt-5" }, "org-a-key");

        var a = await service.ResolveAsync("org-a");
        Assert.Equal(("OpenAI", "gpt-5", "org-a-key"), (a.Provider, a.Model, a.ApiKey));
        Assert.Same(Server, await service.ResolveAsync("org-b"));
        Assert.Same(Server, await service.ResolveAsync(null));

        // Keys are kept per provider: switching away and back needs no new key.
        await service.SaveAsync("org-a", new OrganizationLlmSettings { Provider = "Ollama", Model = "qwen3:8b" }, null);
        await service.SaveAsync("org-a", new OrganizationLlmSettings { Provider = "OpenAI", Model = "gpt-5-mini" }, null);
        Assert.Equal("org-a-key", (await service.ResolveAsync("org-a")).ApiKey);

        await service.ClearAsync("org-a");
        Assert.Same(Server, await service.ResolveAsync("org-a"));
        Assert.False(await service.HasApiKeyAsync("org-a", "OpenAI"));
    }

    [Fact]
    public async Task Calls_reach_the_organizations_provider_or_else_the_servers()
    {
        var serverOptions = Options.Create(new LlmOptions { Provider = "Anthropic", Model = "server-model", ApiKey = "k" });
        var service = new LlmSettingsService(new MemorySecrets(), serverOptions);
        await service.SaveAsync("demo-org", new OrganizationLlmSettings { Provider = "Mock" }, null);

        var server = new CountingProvider();
        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var router = new OrganizationLlmRouter(server, service, new LlmProviderFactory(http, serverOptions, new HeuristicMockLlmProvider()));

        var request = new LlmCompletionRequest { Messages = [ChatMessage.System("s"), ChatMessage.User("hello")] };
        await router.CompleteAsync(request with { TenantId = "other-org" });
        await router.CompleteAsync(request);
        Assert.Equal(2, server.Calls);

        // The organization chose the demo provider: the server's provider isn't called.
        var answer = await router.CompleteAsync(request with { TenantId = "demo-org" });
        Assert.Equal(2, server.Calls);
        Assert.NotEqual(LlmFinishReason.Error, answer.FinishReason);
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
