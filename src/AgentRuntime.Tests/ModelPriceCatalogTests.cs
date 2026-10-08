using AgentRuntime.Configuration;
using AgentRuntime.LLM;

namespace AgentRuntime.Tests;

/// <summary>List prices (docs/llm-settings.md): which model a price belongs to, and the order
/// budgets take prices in: the profile's own, the list price, free for local models, the server's.</summary>
public sealed class ModelPriceCatalogTests
{
    private static readonly LlmOptions Server = new()
    {
        Provider = "Anthropic",
        Model = "claude-sonnet-5-5",
        PricePerInputTokenUsd = 0.000003m,
        PricePerOutputTokenUsd = 0.000015m
    };

    private static ModelProfile Profile(string provider, string model, string? baseUrl = null) =>
        new() { Id = "p", Name = "p", Provider = provider, Model = model, BaseUrl = baseUrl };

    [Theory]
    [InlineData("OpenAI", "gpt-6-astra", "gpt-6-astra")]
    [InlineData("openai", "GPT-6-ASTRA", "gpt-6-astra")]
    [InlineData("OpenAI", "gpt-4o-mini-2024-07-18", "gpt-4o-mini")]
    [InlineData("OpenAI", "gpt-4o-2024-05-13", "gpt-4o-2024-05-13")]
    [InlineData("Anthropic", "claude-haiku-4-5-20251001", "claude-haiku-4-5")]
    [InlineData("Gemini", "models/gemini-2.5-pro", "gemini-2.5-pro")]
    public void Models_and_their_snapshots_are_found(string provider, string model, string listed)
    {
        Assert.Equal(listed, ModelPriceCatalog.Find(provider, model)?.Model);
    }

    [Theory]
    [InlineData("OpenAI", "gpt-5-turbo")]
    [InlineData("OpenAI", "claude-opus-5-5")]
    [InlineData("Ollama", "qwen3:8b")]
    [InlineData("OpenAI", "")]
    public void Unknown_models_have_no_list_price(string provider, string model)
    {
        Assert.Null(ModelPriceCatalog.Find(provider, model));
    }

    [Fact]
    public void Every_listed_model_is_unique_and_priced()
    {
        Assert.Equal(ModelPriceCatalog.All.Count, ModelPriceCatalog.All.Select(p => (p.Provider, p.Model)).Distinct().Count());
        Assert.All(ModelPriceCatalog.All, p =>
        {
            Assert.Contains(p.Provider, LlmProviders.All);
            Assert.True(p.InputPerMillion > 0 && p.OutputPerMillion > 0);
            Assert.True(p.CachedInputPerMillion is null || p.CachedInputPerMillion <= p.InputPerMillion);
        });
    }

    [Fact]
    public void An_unpriced_profile_of_a_listed_model_uses_its_list_price_and_cache_rate()
    {
        var astra = LlmSettingsService.Apply(Server, Profile("OpenAI", "gpt-6-astra"), "k");
        Assert.Equal(0.00001m, astra.PricePerInputTokenUsd);
        Assert.Equal(0.00005m, astra.PricePerOutputTokenUsd);
        Assert.Equal(0.1m, astra.CachedInputPriceFactor);

        var withFast = LlmSettingsService.Apply(Server, Profile("OpenAI", "gpt-6-astra") with { FastModel = "gpt-6-luna" }, "k");
        Assert.Equal(0.0000001m, withFast.FastPricePerInputTokenUsd);
        Assert.Equal(0.0000005m, withFast.FastPricePerOutputTokenUsd);
    }

    [Fact]
    public void Entered_prices_win_and_unlisted_models_fall_back_to_the_server()
    {
        var custom = LlmSettingsService.Apply(Server, Profile("OpenAI", "gpt-6-astra") with { PricePerInputTokenUsd = 0.000001m }, "k");
        Assert.Equal(0.000001m, custom.PricePerInputTokenUsd);
        Assert.Equal(0.00005m, custom.PricePerOutputTokenUsd);

        var unlisted = LlmSettingsService.Apply(Server, Profile("OpenAI", "my-fine-tune"), "k");
        Assert.Equal(Server.PricePerInputTokenUsd, unlisted.PricePerInputTokenUsd);
        Assert.Equal(Server.PricePerOutputTokenUsd, unlisted.PricePerOutputTokenUsd);
    }

    [Fact]
    public void A_custom_base_url_is_another_service_so_list_prices_do_not_apply()
    {
        var proxied = LlmSettingsService.Apply(Server, Profile("OpenAI", "gpt-6-astra", "https://openrouter.ai/api"), "k");
        Assert.Equal(Server.PricePerInputTokenUsd, proxied.PricePerInputTokenUsd);
    }

    [Fact]
    public void The_servers_model_gets_its_list_price_unless_the_configuration_sets_one()
    {
        var listed = new LlmOptions { Provider = "OpenAI", Model = "gpt-6-astra", FastModel = "gpt-6-luna" };
        ModelPriceCatalog.FillServerPrices(listed, _ => false);
        Assert.Equal(0.00001m, listed.PricePerInputTokenUsd);
        Assert.Equal(0.00005m, listed.PricePerOutputTokenUsd);
        Assert.Equal(0.0000001m, listed.FastPricePerInputTokenUsd);

        var configured = new LlmOptions { Provider = "OpenAI", Model = "gpt-6-astra", PricePerInputTokenUsd = 0.000002m };
        ModelPriceCatalog.FillServerPrices(configured, key => key == nameof(LlmOptions.PricePerInputTokenUsd));
        Assert.Equal(0.000002m, configured.PricePerInputTokenUsd);
        Assert.Equal(0.00005m, configured.PricePerOutputTokenUsd);

        var unknown = new LlmOptions { Provider = "OpenAI", Model = "my-fine-tune" };
        ModelPriceCatalog.FillServerPrices(unknown, _ => false);
        Assert.Equal(0.000003m, unknown.PricePerInputTokenUsd);
    }
}
