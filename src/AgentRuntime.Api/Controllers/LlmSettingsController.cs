using AgentRuntime.Api.Platform;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.LLM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The organization's models (docs/llm-settings.md): named profiles, each a provider, a model, its
/// own API key and the prices budgets count in, plus which one tasks use by default. Anyone in the
/// organization can see the models (to pick one for a task); changing them needs the Admin role.
/// Keys are stored encrypted and never returned. The server's configuration is always available
/// as the "server" profile.
/// </summary>
[ApiController]
[Route("api/llm")]
public sealed class LlmSettingsController(LlmSettingsService settings, LlmConnectionTester tester, TenantAccess access) : ControllerBase
{
    public sealed record ProfileBody(
        string? Id,
        string? Name,
        string? Description,
        string Provider,
        string? Model,
        string? FastModel,
        string? BaseUrl,
        string? ApiKey,
        decimal? PricePerInputTokenUsd,
        decimal? PricePerOutputTokenUsd,
        decimal? FastPricePerInputTokenUsd,
        decimal? FastPricePerOutputTokenUsd,
        bool MakeDefault = false);

    public sealed record DefaultBody(string ProfileId);

    public sealed record AgentChoiceBody(bool Enabled);

    public sealed record ModelsBody(string Provider, string? BaseUrl, string? ApiKey, string? ProfileId);

    /// <summary>What the dashboard offers: each provider, whether it needs a key or an address, and
    /// the models it serves with their list prices (any other model id the provider serves works
    /// too, with prices entered for it).</summary>
    [HttpGet("providers")]
    public IActionResult Providers() => Ok(new[]
    {
        Provider("Anthropic", "Anthropic (Claude)", needsKey: true, "https://api.anthropic.com", null, "https://console.anthropic.com/settings/keys"),
        Provider("OpenAI", "OpenAI (or compatible)", needsKey: true, "https://api.openai.com", null, "https://platform.openai.com/api-keys"),
        Provider("Gemini", "Google Gemini", needsKey: true, "https://generativelanguage.googleapis.com", null, "https://aistudio.google.com/apikey"),
        Provider("Ollama", "Ollama (local models)", needsKey: false, LlmProviderFactory.DefaultOllamaUrl().TrimEnd('/'),
            ["qwen3:8b", "qwen2.5:7b", "llama3.1:8b"], "https://ollama.com/search?c=tools"),
        Provider("Mock", "Mock (demo, no model)", needsKey: false, null, ["mock"], null),
    });

    [HttpGet("settings")]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await ViewAsync(ct));

    /// <summary>Adds a model. Its id comes from the name unless given; 409 if the id is taken.</summary>
    [HttpPost("profiles")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Create([FromBody] ProfileBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        var id = string.IsNullOrWhiteSpace(body.Id) ? ModelProfiles.Slug(body.Name ?? body.Model ?? body.Provider) : body.Id.Trim();
        var existing = await settings.GetAsync(access.TenantId, ct);
        if (existing.Profiles.Any(p => p.Id == id)) return Conflict(new { error = $"There's already a model with the id '{id}'. Pick another name." });
        return await SaveAsync(id, body, isNew: true, ct);
    }

    /// <summary>Edits a model. A key left out keeps the saved one.</summary>
    [HttpPut("profiles/{id}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Update(string id, [FromBody] ProfileBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        var existing = await settings.GetAsync(access.TenantId, ct);
        if (existing.Profiles.All(p => p.Id != id)) return NotFound(new { error = $"No model '{id}'." });
        return await SaveAsync(id, body, isNew: false, ct);
    }

    /// <summary>Removes a model and its key. Running tasks on it continue on the default.</summary>
    [HttpDelete("profiles/{id}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct) =>
        await settings.DeleteProfileAsync(access.TenantId, id, ct) ? Ok(await ViewAsync(ct)) : NotFound(new { error = $"No model '{id}'." });

    /// <summary>Which model tasks use unless they pick one; "server" for the server's configuration.</summary>
    [HttpPut("default")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetDefault([FromBody] DefaultBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        return await settings.SetDefaultAsync(access.TenantId, body.ProfileId ?? string.Empty, ct)
            ? Ok(await ViewAsync(ct))
            : NotFound(new { error = $"No model '{body.ProfileId}'." });
    }

    /// <summary>Whether agents may pick one of the organization's models for the agents they spawn
    /// (following the goal, or the models' descriptions). Off: every agent runs on the task's model.</summary>
    [HttpPut("agent-choice")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetAgentChoice([FromBody] AgentChoiceBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        await settings.SetAgentsMayChooseAsync(access.TenantId, body.Enabled, ct);
        return Ok(await ViewAsync(ct));
    }

    /// <summary>Back to the server's configuration: every model and saved key is deleted.</summary>
    [HttpDelete("settings")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        await settings.ClearAsync(access.TenantId, ct);
        return Ok(await ViewAsync(ct));
    }

    /// <summary>One tiny call with the given settings (with an id, that model's saved key when no key
    /// is given), so a wrong key, model or address shows up before agents depend on it.</summary>
    [HttpPost("test")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Test([FromBody] ProfileBody body, CancellationToken ct)
    {
        if (Disabled() is { } off) return off;
        var (profile, error) = Validate(body.Id ?? "test", body);
        if (profile is null) return BadRequest(new { error });

        var options = await CandidateAsync(profile, body.ApiKey, body.Id, ct);
        if (LlmProviders.NeedsApiKey(profile.Provider) && string.IsNullOrEmpty(options.ApiKey))
        {
            return BadRequest(new { error = $"Enter your {profile.Provider} API key to test it." });
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

        var options = await CandidateAsync(new ModelProfile { Id = "list", Name = "list", Provider = provider, BaseUrl = Blank(body.BaseUrl) },
            body.ApiKey, body.ProfileId, ct);
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

    private async Task<IActionResult> SaveAsync(string id, ProfileBody body, bool isNew, CancellationToken ct)
    {
        if (!ModelProfiles.IsValidId(id) || id == ModelProfiles.ServerId)
        {
            return BadRequest(new { error = "A model id uses lowercase letters, digits and dashes (and isn't \"server\")." });
        }

        var (profile, error) = Validate(id, body);
        if (profile is null) return BadRequest(new { error });

        if (!isNew && string.IsNullOrWhiteSpace(body.ApiKey)
            && (await settings.GetAsync(access.TenantId, ct)).Profiles.FirstOrDefault(p => p.Id == id) is { } saved
            && !SameEndpoint(saved, profile) && await settings.HasApiKeyAsync(access.TenantId, saved, ct))
        {
            // The saved key belongs to the old provider or address; it isn't carried over to a new one.
            return BadRequest(new { error = "The provider or address changed: enter the API key for it again." });
        }

        var hasKey = !string.IsNullOrWhiteSpace(body.ApiKey) || !isNew && await settings.HasApiKeyAsync(access.TenantId, profile, ct);
        if (LlmProviders.NeedsApiKey(profile.Provider) && !hasKey && !UsesServerKey(profile))
        {
            return BadRequest(new { error = $"{profile.Provider} needs an API key." });
        }

        var caller = HttpContext.Caller();
        await settings.SaveProfileAsync(access.TenantId, profile with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = caller.Email ?? caller.UserId ?? "api key"
        }, body.ApiKey, body.MakeDefault, ct);
        return Ok(await ViewAsync(ct));
    }

    private async Task<object> ViewAsync(CancellationToken ct)
    {
        var server = settings.Server;
        var org = server.AllowOrganizationSettings ? await settings.GetAsync(access.TenantId, ct) : new OrganizationModels();
        var profiles = new List<object>();
        foreach (var p in org.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var hasKey = await settings.HasApiKeyAsync(access.TenantId, p, ct);
            var o = LlmSettingsService.Apply(server, p, apiKey: null);
            profiles.Add(new
            {
                id = p.Id,
                name = p.Name,
                description = p.Description,
                provider = p.Provider,
                model = p.Model,
                fast_model = p.FastModel,
                base_url = p.BaseUrl,
                price_per_input_token_usd = p.PricePerInputTokenUsd,
                price_per_output_token_usd = p.PricePerOutputTokenUsd,
                fast_price_per_input_token_usd = p.FastPricePerInputTokenUsd,
                fast_price_per_output_token_usd = p.FastPricePerOutputTokenUsd,
                // What budgets count with, per million tokens as providers publish them.
                price_per_million_input_usd = o.PricePerInputTokenUsd * 1_000_000,
                price_per_million_output_usd = o.PricePerOutputTokenUsd * 1_000_000,
                price_source = PriceSource(p),
                api_key_set = hasKey,
                uses_server_key = !hasKey && UsesServerKey(p),
                is_default = org.DefaultProfileId == p.Id,
                updated_at = p.UpdatedAt,
                updated_by = p.UpdatedBy
            });
        }

        var effective = await settings.ResolveAsync(access.TenantId, cancellationToken: ct);
        return new
        {
            allow_organization_settings = server.AllowOrganizationSettings,
            agents_may_choose = org.AgentsMayChoose,
            default_profile_id = effective.ProfileId ?? ModelProfiles.ServerId,
            server = new
            {
                id = ModelProfiles.ServerId,
                name = "Server default",
                provider = server.Provider,
                model = server.Model,
                fast_model = Blank(server.FastModel),
                price_per_million_input_usd = server.PricePerInputTokenUsd * 1_000_000,
                price_per_million_output_usd = server.PricePerOutputTokenUsd * 1_000_000,
                is_default = effective.ProfileId is null
            },
            profiles,
            effective = new
            {
                profile_id = effective.ProfileId ?? ModelProfiles.ServerId,
                name = effective.ProfileName ?? "Server default",
                provider = effective.Provider,
                model = effective.Model,
                fast_model = Blank(effective.FastModel),
                price_per_million_input_usd = effective.PricePerInputTokenUsd * 1_000_000,
                price_per_million_output_usd = effective.PricePerOutputTokenUsd * 1_000_000
            }
        };
    }

    /// <summary>The options a test or model listing runs with: the server's with this profile on
    /// top, and the given key, else the saved key of the named profile.</summary>
    private async Task<LlmOptions> CandidateAsync(ModelProfile profile, string? apiKey, string? savedProfileId, CancellationToken ct)
    {
        var key = Blank(apiKey);
        if (key is null && savedProfileId is not null)
        {
            var org = await settings.GetAsync(access.TenantId, ct);
            // A saved key is reused only for the provider and address it was saved with: it is never
            // sent somewhere new without being entered again.
            if (org.Profiles.FirstOrDefault(p => p.Id == savedProfileId) is { } saved && SameEndpoint(saved, profile))
            {
                key = await settings.GetApiKeyAsync(access.TenantId, saved, ct);
            }
        }

        return LlmSettingsService.Apply(settings.Server, profile, key);
    }

    private static bool SameEndpoint(ModelProfile a, ModelProfile b) =>
        a.Provider == b.Provider && string.Equals(a.BaseUrl?.TrimEnd('/') ?? string.Empty, b.BaseUrl?.TrimEnd('/') ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>The server's key may stand in only for its own provider at its own address.</summary>
    private bool UsesServerKey(ModelProfile profile) =>
        !string.IsNullOrEmpty(LlmSettingsService.Apply(settings.Server, profile, apiKey: null).ApiKey);

    private IActionResult? Disabled() => settings.Server.AllowOrganizationSettings
        ? null
        : StatusCode(StatusCodes.Status403Forbidden, new { error = "This server sets the model for every organization (Llm:AllowOrganizationSettings is off)." });

    private static (ModelProfile? Profile, string? Error) Validate(string id, ProfileBody body)
    {
        var provider = LlmProviders.Canonical(body.Provider);
        if (provider is null) return (null, $"Unknown provider '{body.Provider}'. Use one of: {string.Join(", ", LlmProviders.All)}.");

        var model = Blank(body.Model);
        if (model is null && provider != "Mock") return (null, "Choose a model.");
        if (model?.Length > 200 || body.FastModel?.Length > 200) return (null, "That model name is too long.");
        var name = Blank(body.Name) ?? model ?? provider;
        if (name.Length > 80) return (null, "Keep the name under 80 characters.");
        if (body.Description?.Length > 200) return (null, "Keep the description under 200 characters.");
        if (BaseUrlError(body.BaseUrl) is { } urlError) return (null, urlError);

        decimal?[] prices = [body.PricePerInputTokenUsd, body.PricePerOutputTokenUsd, body.FastPricePerInputTokenUsd, body.FastPricePerOutputTokenUsd];
        // $1 per token is far beyond any real model: almost certainly a per-million price typed in.
        if (prices.Any(p => p is < 0 or > 1m)) return (null, "Prices are in USD per token, between 0 and 1 (e.g. 0.000003 for $3 per million).");

        return (new ModelProfile
        {
            Id = id,
            Name = name,
            Description = Blank(body.Description),
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

    /// <summary>A provider for the dashboard. Without <paramref name="models"/> its models are the
    /// listed ones (<see cref="ModelPriceCatalog"/>), with their prices.</summary>
    private static object Provider(string id, string label, bool needsKey, string? defaultBaseUrl, string[]? models, string? keyUrl)
    {
        var listed = ModelPriceCatalog.For(id).ToList();
        return new
        {
            id,
            label,
            needs_api_key = needsKey,
            free = LlmProviders.IsFree(id),
            default_base_url = defaultBaseUrl,
            suggested_models = models ?? listed.Select(m => m.Model).ToArray(),
            models = listed.Select(m => new
            {
                id = m.Model,
                name = m.Name,
                input_per_million_usd = m.InputPerMillion,
                output_per_million_usd = m.OutputPerMillion,
                cached_input_per_million_usd = m.CachedInputPerMillion,
                note = m.Note
            }),
            prices_as_of = listed.Count > 0 ? ModelPriceCatalog.AsOf : null,
            pricing_url = ModelPriceCatalog.SourceUrl(id),
            get_key_url = keyUrl
        };
    }

    /// <summary>Where the price budgets count with comes from: "custom" (entered for the model),
    /// "list" (the provider's list price), "free" (a local model) or "server" (the server's
    /// prices, standing in for a model nobody priced).</summary>
    private static string PriceSource(ModelProfile p)
    {
        if (p.PricePerInputTokenUsd is not null || p.PricePerOutputTokenUsd is not null) return "custom";
        if (LlmProviders.IsFree(p.Provider)) return "free";
        return LlmSettingsService.ListPrices(p.Provider, p.Model, null, p.BaseUrl).Model is not null ? "list" : "server";
    }
}
