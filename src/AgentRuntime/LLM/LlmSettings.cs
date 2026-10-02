using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRuntime.Configuration;
using AgentRuntime.Integrations;
using Microsoft.Extensions.Options;

namespace AgentRuntime.LLM;

/// <summary>
/// One model an organization set up (docs/llm-settings.md): a provider, a model id, and the prices
/// budgets are counted with. An organization can have several, e.g. a strong one for planning-heavy
/// work and a cheap or local one for routine tasks, and pick one per task. Each profile's API key
/// is stored apart, encrypted, and never returned.
/// </summary>
public sealed record ModelProfile
{
    /// <summary>Stable id (lowercase letters, digits, dashes); tasks refer to profiles by it.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public string Model { get; init; } = string.Empty;
    public string? FastModel { get; init; }
    public string? BaseUrl { get; init; }

    /// <summary>USD per token. Budgets and spend are counted with these, so they should match the
    /// provider's price list for the model.</summary>
    public decimal? PricePerInputTokenUsd { get; init; }
    public decimal? PricePerOutputTokenUsd { get; init; }
    public decimal? FastPricePerInputTokenUsd { get; init; }
    public decimal? FastPricePerOutputTokenUsd { get; init; }

    /// <summary>A short note shown when picking a model, e.g. "best for research".</summary>
    public string? Description { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}

/// <summary>An organization's models and which one tasks use unless they pick another.</summary>
public sealed record OrganizationModels
{
    public List<ModelProfile> Profiles { get; init; } = [];

    /// <summary>The profile used when a task names none; null or <see cref="ModelProfiles.ServerId"/>
    /// means the server's configuration.</summary>
    public string? DefaultProfileId { get; init; }
}

public static partial class ModelProfiles
{
    /// <summary>The server's own configuration, offered next to an organization's profiles.</summary>
    public const string ServerId = "server";

    /// <summary>A profile id from a name: "GPT-5 mini (cheap)" → "gpt-5-mini-cheap".</summary>
    public static string Slug(string name)
    {
        var slug = SlugChars().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return slug.Length == 0 || slug == ServerId ? "model" : slug;
    }

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 40 } && ValidId().IsMatch(id);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugChars();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidId();
}

/// <summary>The providers the runtime can talk to, and what each needs.</summary>
public static class LlmProviders
{
    public static readonly IReadOnlyList<string> All = ["Anthropic", "OpenAI", "Gemini", "Ollama", "Mock"];

    /// <summary>Normalizes a provider name to its canonical spelling, or null if unknown.</summary>
    public static string? Canonical(string? name) =>
        All.FirstOrDefault(p => p.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool NeedsApiKey(string provider) => provider is "Anthropic" or "OpenAI" or "Gemini";

    /// <summary>Runs on the caller's own hardware, or is the built-in demo: free per token.</summary>
    public static bool IsFree(string provider) => provider is "Ollama" or "Mock";
}

/// <summary>The model settings in force for a call.</summary>
public interface ILlmSettingsResolver
{
    /// <summary>The server's options with an organization's model profile applied: the named one,
    /// else the organization's default, else the server's configuration.</summary>
    Task<LlmOptions> ResolveAsync(string? tenantId, string? profileId = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Which model profile a task uses. Chosen when the task starts and changeable while it runs: every
/// agent of the task reads it at the start of each step, so a switch reaches the whole team,
/// including agents spawned later. Null means the organization's default.
/// </summary>
public interface ITaskModelSelection
{
    Task<string?> GetAsync(string taskId, CancellationToken cancellationToken = default);
    Task SetAsync(string taskId, string? profileId, CancellationToken cancellationToken = default);
}

/// <summary>Task model choices kept in memory (tests and the standalone host).</summary>
public sealed class InMemoryTaskModelSelection : ITaskModelSelection
{
    private readonly ConcurrentDictionary<string, string> _choices = new();

    public Task<string?> GetAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_choices.TryGetValue(taskId, out var id) ? id : null);

    public Task SetAsync(string taskId, string? profileId, CancellationToken cancellationToken = default)
    {
        if (profileId is null) _choices.TryRemove(taskId, out _);
        else _choices[taskId] = profileId;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stores organization model profiles in the encrypted secret store (the list, and each profile's
/// key, under the organization's own scope) and resolves the options each call uses. Resolved
/// options are cached briefly; saving through this service refreshes them at once on this server.
/// </summary>
public sealed class LlmSettingsService(ISecretStore secrets, IOptions<LlmOptions> serverOptions) : ILlmSettingsResolver
{
    private const string ModelsKey = "models";
    /// <summary>The single-model settings saved before profiles existed; read as one profile.</summary>
    private const string LegacySettingsKey = "settings";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, OrganizationModels Models)> _cache = new();
    private readonly ConcurrentDictionary<(string Tenant, string Profile), (DateTimeOffset At, LlmOptions Options)> _resolved = new();

    public LlmOptions Server => serverOptions.Value;

    private static string Scope(string tenantId) => $"llm:{Tenancy.TenantIds.Normalize(tenantId)}";
    private static string KeyName(string profileId) => $"api_key:profile:{profileId}";
    private static string LegacyKeyName(string provider) => $"api_key:{provider}";

    public async Task<OrganizationModels> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = Tenancy.TenantIds.Normalize(tenantId);
        if (_cache.TryGetValue(tenant, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) return hit.Models;

        var models = await LoadAsync(tenant, ct);
        _cache[tenant] = (DateTimeOffset.UtcNow, models);
        return models;
    }

    private async Task<OrganizationModels> LoadAsync(string tenant, CancellationToken ct)
    {
        if (await secrets.GetAsync(Scope(tenant), ModelsKey, ct) is { Length: > 0 } json)
        {
            return JsonSerializer.Deserialize<OrganizationModels>(json, Json) ?? new OrganizationModels();
        }

        // Saved before profiles: one model, which was the organization's choice for everything.
        if (await secrets.GetAsync(Scope(tenant), LegacySettingsKey, ct) is { Length: > 0 } legacy
            && JsonSerializer.Deserialize<LegacySettings>(legacy, Json) is { } old)
        {
            var profile = new ModelProfile
            {
                Id = ModelProfiles.Slug(old.Model is { Length: > 0 } m ? m : old.Provider),
                Name = old.Model is { Length: > 0 } name ? name : old.Provider,
                Provider = old.Provider,
                Model = old.Model ?? string.Empty,
                FastModel = old.FastModel,
                BaseUrl = old.BaseUrl,
                PricePerInputTokenUsd = old.PricePerInputTokenUsd,
                PricePerOutputTokenUsd = old.PricePerOutputTokenUsd,
                FastPricePerInputTokenUsd = old.FastPricePerInputTokenUsd,
                FastPricePerOutputTokenUsd = old.FastPricePerOutputTokenUsd,
                UpdatedAt = old.UpdatedAt,
                UpdatedBy = old.UpdatedBy
            };
            // Moved over once: the key now belongs to this profile only, so it can never be sent
            // to another profile's address.
            if (await secrets.GetAsync(Scope(tenant), LegacyKeyName(old.Provider), ct) is { Length: > 0 } oldKey)
            {
                await secrets.PutAsync(Scope(tenant), KeyName(profile.Id), oldKey, ct);
                await secrets.PutAsync(Scope(tenant), LegacyKeyName(old.Provider), string.Empty, ct);
            }

            var migrated = new OrganizationModels { Profiles = [profile], DefaultProfileId = profile.Id };
            await secrets.PutAsync(Scope(tenant), ModelsKey, JsonSerializer.Serialize(migrated, Json), ct);
            await secrets.PutAsync(Scope(tenant), LegacySettingsKey, string.Empty, ct);
            return migrated;
        }

        return new OrganizationModels();
    }

    private sealed record LegacySettings(string Provider, string? Model, string? FastModel, string? BaseUrl,
        decimal? PricePerInputTokenUsd, decimal? PricePerOutputTokenUsd, decimal? FastPricePerInputTokenUsd, decimal? FastPricePerOutputTokenUsd,
        DateTimeOffset UpdatedAt, string? UpdatedBy);

    /// <summary>True when this profile has a key of its own.</summary>
    public async Task<bool> HasApiKeyAsync(string tenantId, ModelProfile profile, CancellationToken ct = default) =>
        await GetApiKeyAsync(tenantId, profile, ct) is not null;

    public async Task<string?> GetApiKeyAsync(string tenantId, ModelProfile profile, CancellationToken ct = default) =>
        await secrets.GetAsync(Scope(tenantId), KeyName(profile.Id), ct) is { Length: > 0 } key ? key : null;

    /// <summary>Adds or replaces a profile (by id) and, when given, its key.</summary>
    public async Task SaveProfileAsync(string tenantId, ModelProfile profile, string? apiKey, bool makeDefault, CancellationToken ct = default)
    {
        var tenant = Tenancy.TenantIds.Normalize(tenantId);
        var current = await LoadAsync(tenant, ct);
        var profiles = current.Profiles.Where(p => p.Id != profile.Id).Append(profile).ToList();
        var defaultId = makeDefault || current.Profiles.Count == 0 && current.DefaultProfileId is null ? profile.Id : current.DefaultProfileId;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await secrets.PutAsync(Scope(tenant), KeyName(profile.Id), apiKey.Trim(), ct);
        }

        await WriteAsync(tenant, new OrganizationModels { Profiles = profiles, DefaultProfileId = defaultId }, ct);
    }

    /// <summary>Removes a profile and its key. Tasks that used it continue on the default.</summary>
    public async Task<bool> DeleteProfileAsync(string tenantId, string profileId, CancellationToken ct = default)
    {
        var tenant = Tenancy.TenantIds.Normalize(tenantId);
        var current = await LoadAsync(tenant, ct);
        if (current.Profiles.All(p => p.Id != profileId)) return false;

        await secrets.PutAsync(Scope(tenant), KeyName(profileId), string.Empty, ct);
        await WriteAsync(tenant, new OrganizationModels
        {
            Profiles = current.Profiles.Where(p => p.Id != profileId).ToList(),
            DefaultProfileId = current.DefaultProfileId == profileId ? null : current.DefaultProfileId
        }, ct);
        return true;
    }

    /// <summary>Which profile tasks use unless they pick one; <see cref="ModelProfiles.ServerId"/>
    /// for the server's configuration.</summary>
    public async Task<bool> SetDefaultAsync(string tenantId, string profileId, CancellationToken ct = default)
    {
        var tenant = Tenancy.TenantIds.Normalize(tenantId);
        var current = await LoadAsync(tenant, ct);
        if (profileId != ModelProfiles.ServerId && current.Profiles.All(p => p.Id != profileId)) return false;

        await WriteAsync(tenant, current with { DefaultProfileId = profileId == ModelProfiles.ServerId ? null : profileId }, ct);
        return true;
    }

    /// <summary>Back to the server configuration: every profile and saved key is deleted.</summary>
    public async Task ClearAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = Tenancy.TenantIds.Normalize(tenantId);
        await secrets.DeleteScopeAsync(Scope(tenant), ct);
        Forget(tenant);
    }

    private async Task WriteAsync(string tenant, OrganizationModels models, CancellationToken ct)
    {
        await secrets.PutAsync(Scope(tenant), ModelsKey, JsonSerializer.Serialize(models, Json), ct);
        Forget(tenant);
    }

    private void Forget(string tenant)
    {
        _cache.TryRemove(tenant, out _);
        foreach (var key in _resolved.Keys.Where(k => k.Tenant == tenant)) _resolved.TryRemove(key, out _);
    }

    public async Task<LlmOptions> ResolveAsync(string? tenantId, string? profileId = null, CancellationToken cancellationToken = default)
    {
        var server = serverOptions.Value;
        if (!server.AllowOrganizationSettings || string.IsNullOrWhiteSpace(tenantId)) return server;

        var tenant = Tenancy.TenantIds.Normalize(tenantId);
        var cacheKey = (tenant, profileId ?? string.Empty);
        if (_resolved.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) return hit.Options;

        var models = await GetAsync(tenant, cancellationToken);
        // The named profile, else the organization's default (also when the named one was deleted
        // mid-task), else the server's configuration.
        var profile = profileId == ModelProfiles.ServerId ? null
            : models.Profiles.FirstOrDefault(p => p.Id == profileId)
              ?? models.Profiles.FirstOrDefault(p => p.Id == models.DefaultProfileId);
        var resolved = profile is null
            ? server
            : Apply(server, profile, await GetApiKeyAsync(tenant, profile, cancellationToken));
        _resolved[cacheKey] = (DateTimeOffset.UtcNow, resolved);
        return resolved;
    }

    /// <summary>
    /// The server's options with a profile on top. Without a key of its own the profile uses the
    /// server's key, but only for the server's own provider at the server's own address: a key is
    /// never sent anywhere an organization chose.
    /// </summary>
    public static LlmOptions Apply(LlmOptions server, ModelProfile profile, string? apiKey)
    {
        var o = server.Clone();
        var sameProvider = profile.Provider.Equals(server.Provider, StringComparison.OrdinalIgnoreCase);
        var sameAddress = string.IsNullOrWhiteSpace(profile.BaseUrl)
                          || string.Equals(profile.BaseUrl?.TrimEnd('/'), server.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        o.ProfileId = profile.Id;
        o.ProfileName = profile.Name;
        o.Provider = profile.Provider;
        o.Model = string.IsNullOrWhiteSpace(profile.Model) ? (sameProvider ? server.Model : string.Empty) : profile.Model.Trim();
        o.FastModel = string.IsNullOrWhiteSpace(profile.FastModel) ? null : profile.FastModel.Trim();
        o.BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? (sameProvider ? server.BaseUrl : null) : profile.BaseUrl.Trim();
        o.ApiKey = apiKey ?? (sameProvider && sameAddress ? server.ApiKey : null);

        // Prices: the profile's, else free for local models, else the server's as the closest estimate.
        var free = LlmProviders.IsFree(profile.Provider);
        o.PricePerInputTokenUsd = profile.PricePerInputTokenUsd ?? (free ? 0 : server.PricePerInputTokenUsd);
        o.PricePerOutputTokenUsd = profile.PricePerOutputTokenUsd ?? (free ? 0 : server.PricePerOutputTokenUsd);
        o.FastPricePerInputTokenUsd = profile.FastPricePerInputTokenUsd ?? (free ? 0 : null);
        o.FastPricePerOutputTokenUsd = profile.FastPricePerOutputTokenUsd ?? (free ? 0 : null);
        if (!sameProvider)
        {
            // The server's cache-price factors were set for its own provider.
            o.CachedInputPriceFactor = null;
            o.CacheWritePriceFactor = null;
        }

        return o;
    }
}
