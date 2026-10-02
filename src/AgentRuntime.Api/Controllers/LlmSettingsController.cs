using AgentRuntime.Api.Platform;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.LLM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The organization's model settings (docs/llm-settings.md): which provider and model its agents
/// use, its own API key, and the prices its budgets count in. Anyone in the organization can see
/// which model is in use; changing it needs the Admin role. Keys are stored encrypted and never
/// returned. Without settings of its own, an organization uses the server's configuration.
/// </summary>
[ApiController]
[Route("api/llm")]
public sealed class LlmSettingsController(LlmSettingsService settings, LlmConnectionTester tester, TenantAccess access) : ControllerBase
{
    public sealed record SettingsBody(
        string Provider,
        string? Model,
        string? FastModel,
        string? BaseUrl,
        string? ApiKey,
        decimal? PricePerInputTokenUsd,
        decimal? PricePerOutputTokenUsd,
        decimal? FastPricePerInputTokenUsd,
        decimal? FastPricePerOutputTokenUsd);

    public sealed record ModelsBody(string Provider, string? BaseUrl, string? ApiKey);

    /// <summary>What the dashboard offers: each provider, whether it needs a key or an address,
    /// and a few well-known models to start from (any model id the provider serves works).</summary>
    [HttpGet("providers")]
    public IActionResult Providers() => Ok(new[]
    {
        Provider("Anthropic", "Anthropic (Claude)", needsKey: true, "https://api.anthropic.com",
            ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001"], "https://console.anthropic.com/settings/keys"),
        Provider("OpenAI", "OpenAI (or compatible)", needsKey: true, "https://api.openai.com",
            ["gpt-5", "gpt-5-mini", "gpt-4.1", "gpt-4o-mini"], "https://platform.openai.com/api-keys"),
        Provider("Gemini", "Google Gemini", needsKey: true, "https://generativelanguage.googleapis.com",
            ["gemini-2.5-pro", "gemini-2.5-flash"], "https://aistudio.google.com/apikey"),
        Provider("Ollama", "Ollama (local models)", needsKey: false, LlmProviderFactory.DefaultOllamaUrl().TrimEnd('/'),
            ["qwen3:8b", "qwen2.5:7b", "llama3.1:8b"], "https://ollama.com/search?c=tools"),
        Provider("Mock", "Mock (demo, no model)", needsKey: false, null, ["mock"], null),
    });

    [HttpGet("settings")]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await ViewAsync(ct));

    [HttpPut("settings")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Save([FromBody] SettingsBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        var (org, error) = Validate(body);
        if (org is null) return BadRequest(new { error });

        var hasKey = !string.IsNullOrWhiteSpace(body.ApiKey) || await settings.HasApiKeyAsync(access.TenantId, org.Provider, ct);
        if (LlmProviders.NeedsApiKey(org.Provider) && !hasKey && !UsesServerKey(org))
        {
            return BadRequest(new { error = $"{org.Provider} needs an API key." });
        }

        await settings.SaveAsync(access.TenantId, org with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = HttpContext.Caller().Email ?? HttpContext.Caller().UserId ?? "api key"
        }, body.ApiKey, ct);
        return Ok(await ViewAsync(ct));
    }

    /// <summary>Back to the server's configuration. The organization's saved keys are deleted.</summary>
    [HttpDelete("settings")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        await settings.ClearAsync(access.TenantId, ct);
        return Ok(await ViewAsync(ct));
    }

    /// <summary>One tiny call with the given settings (the saved key when none is given), so a
    /// wrong key, model or address shows up before agents depend on it.</summary>
    [HttpPost("test")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Test([FromBody] SettingsBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        var (org, error) = Validate(body);
        if (org is null) return BadRequest(new { error });

        var options = await CandidateAsync(org, body.ApiKey, ct);
        if (LlmProviders.NeedsApiKey(org.Provider) && string.IsNullOrEmpty(options.ApiKey))
        {
            return BadRequest(new { error = $"Enter your {org.Provider} API key to test it." });
        }

        var result = await tester.TestAsync(options, ct);
        return Ok(new { ok = result.Ok, message = result.Message, latency_ms = result.LatencyMs, input_tokens = result.InputTokens, output_tokens = result.OutputTokens });
    }

    /// <summary>The models the provider serves, for the model picker.</summary>
    [HttpPost("models")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Models([FromBody] ModelsBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        var provider = LlmProviders.Canonical(body.Provider);
        if (provider is null) return BadRequest(new { error = $"Unknown provider '{body.Provider}'." });
        if (BaseUrlError(body.BaseUrl) is { } urlError) return BadRequest(new { error = urlError });

        var options = await CandidateAsync(new OrganizationLlmSettings { Provider = provider, BaseUrl = Blank(body.BaseUrl) }, body.ApiKey, ct);
        if (LlmProviders.NeedsApiKey(provider) && string.IsNullOrEmpty(options.ApiKey))
        {
            return BadRequest(new { error = $"Enter your {provider} API key to list its models." });
        }

        try
        {
            return Ok(new { models = await tester.ListModelsAsync(options, ct) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Couldn't list {provider}'s models: {ex.Message}" });
        }
    }

    private async Task<object> ViewAsync(CancellationToken ct)
    {
        var server = settings.Server;
        var org = server.AllowOrganizationSettings ? await settings.GetAsync(access.TenantId, ct) : null;
        var effective = await settings.ResolveAsync(access.TenantId, ct);
        return new
        {
            allow_organization_settings = server.AllowOrganizationSettings,
            source = org is null ? "server" : "organization",
            server = new { provider = server.Provider, model = server.Model, fast_model = Blank(server.FastModel) },
            organization = org is null ? null : new
            {
                provider = org.Provider,
                model = org.Model,
                fast_model = org.FastModel,
                base_url = org.BaseUrl,
                price_per_input_token_usd = org.PricePerInputTokenUsd,
                price_per_output_token_usd = org.PricePerOutputTokenUsd,
                fast_price_per_input_token_usd = org.FastPricePerInputTokenUsd,
                fast_price_per_output_token_usd = org.FastPricePerOutputTokenUsd,
                api_key_set = await settings.HasApiKeyAsync(access.TenantId, org.Provider, ct),
                uses_server_key = UsesServerKey(org) && !await settings.HasApiKeyAsync(access.TenantId, org.Provider, ct),
                updated_at = org.UpdatedAt,
                updated_by = org.UpdatedBy
            },
            effective = new
            {
                provider = effective.Provider,
                model = effective.Model,
                fast_model = Blank(effective.FastModel),
                price_per_input_token_usd = effective.PricePerInputTokenUsd,
                price_per_output_token_usd = effective.PricePerOutputTokenUsd,
                // Per million tokens, as providers publish them.
                price_per_million_input_usd = effective.PricePerInputTokenUsd * 1_000_000,
                price_per_million_output_usd = effective.PricePerOutputTokenUsd * 1_000_000
            }
        };
    }

    /// <summary>The options a test or model listing runs with: the server's with these settings
    /// on top, and the given key, else the organization's saved key for the provider.</summary>
    private async Task<LlmOptions> CandidateAsync(OrganizationLlmSettings org, string? apiKey, CancellationToken ct) =>
        LlmSettingsService.Apply(settings.Server, org,
            Blank(apiKey) ?? await settings.GetApiKeyAsync(access.TenantId, org.Provider, ct));

    /// <summary>The server's key may stand in only for its own provider at its own address.</summary>
    private bool UsesServerKey(OrganizationLlmSettings org) =>
        !string.IsNullOrEmpty(LlmSettingsService.Apply(settings.Server, org, apiKey: null).ApiKey);

    private IActionResult? Disabled() => settings.Server.AllowOrganizationSettings
        ? null
        : StatusCode(StatusCodes.Status403Forbidden, new { error = "This server sets the model for every organization (Llm:AllowOrganizationSettings is off)." });

    private static (OrganizationLlmSettings? Settings, string? Error) Validate(SettingsBody body)
    {
        var provider = LlmProviders.Canonical(body.Provider);
        if (provider is null) return (null, $"Unknown provider '{body.Provider}'. Use one of: {string.Join(", ", LlmProviders.All)}.");

        var model = Blank(body.Model);
        if (model is null && provider != "Mock") return (null, "Choose a model.");
        if (model?.Length > 200 || body.FastModel?.Length > 200) return (null, "That model name is too long.");
        if (BaseUrlError(body.BaseUrl) is { } urlError) return (null, urlError);

        decimal?[] prices = [body.PricePerInputTokenUsd, body.PricePerOutputTokenUsd, body.FastPricePerInputTokenUsd, body.FastPricePerOutputTokenUsd];
        // $1 per token is far beyond any real model: almost certainly a per-million price typed in.
        if (prices.Any(p => p is < 0 or > 1m)) return (null, "Prices are in USD per token, between 0 and 1 (e.g. 0.000003 for $3 per million).");

        return (new OrganizationLlmSettings
        {
            Provider = provider,
            Model = model ?? string.Empty,
            FastModel = Blank(body.FastModel),
            BaseUrl = Blank(body.BaseUrl),
            PricePerInputTokenUsd = body.PricePerInputTokenUsd,
            PricePerOutputTokenUsd = body.PricePerOutputTokenUsd,
            FastPricePerInputTokenUsd = body.FastPricePerInputTokenUsd,
            FastPricePerOutputTokenUsd = body.FastPricePerOutputTokenUsd
        }, null);
    }

    private static string? BaseUrlError(string? baseUrl) =>
        Blank(baseUrl) is { } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.Length > 500)
            ? "The address must be a full http(s) URL, e.g. http://localhost:11434."
            : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static object Provider(string id, string label, bool needsKey, string? defaultBaseUrl, string[] models, string? keyUrl) => new
    {
        id,
        label,
        needs_api_key = needsKey,
        free = LlmProviders.IsFree(id),
        default_base_url = defaultBaseUrl,
        suggested_models = models,
        get_key_url = keyUrl
    };
}
