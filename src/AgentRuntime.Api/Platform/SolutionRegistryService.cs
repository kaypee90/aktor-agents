using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Secrets;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Solutions;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// The solution registry (docs/solutions.md#the-registry): organizations publish versions of their
/// packages, and any organization the package is visible to installs one by name. A package name
/// belongs to the organization that first published it. Versions are immutable: a published version
/// can be yanked (no new installs) but never changed. Each version is stored with its SHA-256 and
/// the registry's signature over name, version and digest; installing checks both, so a package
/// altered in the database after publishing is refused.
/// </summary>
public sealed class SolutionRegistryService(IDbContextFactory<AgentDbContext> dbFactory, SolutionService solutions, SecretProtector protector, TimeProvider clock)
{
    private const string SignaturePurpose = "solution-registry-package/v1";

    public static string Digest(string bundleJson) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bundleJson))).ToLowerInvariant();

    private string Sign(string name, string version, string digest) =>
        Convert.ToHexString(protector.Sign(SignaturePurpose, $"{name}@{version}:{digest}")).ToLowerInvariant();

    private bool Verify(SolutionPackageRecord p) =>
        Digest(p.BundleJson) == p.Sha256 &&
        protector.Verify(SignaturePurpose, $"{p.Name}@{p.Version}:{p.Sha256}", Convert.FromHexString(p.Signature));

    private static bool VisibleTo(SolutionPackageRecord p, string tenantId) =>
        p.Visibility == "public" || Tenancy.TenantIds.Same(p.PublisherTenantId, tenantId);

    public async Task<object> PublishAsync(string tenantId, string by, SolutionBundle bundle, string visibility, CancellationToken ct)
    {
        visibility = visibility?.ToLowerInvariant() is "public" ? "public" : "organization";
        var check = solutions.Check(bundle);
        if (!check.Validation.Ok || check.Manifest is not { } manifest) throw new SolutionException("The package isn't valid.", 400, check.Validation.Errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var owner = await db.SolutionPackageNames.FirstOrDefaultAsync(n => n.Name == manifest.Name, ct);
        if (owner is not null && !Tenancy.TenantIds.Same(owner.TenantId, tenantId))
        {
            throw new SolutionException($"'{manifest.Name}' is published by another organization; pick another name.", StatusCodes.Status409Conflict);
        }

        var versions = await db.SolutionPackages.AsNoTracking().Where(p => p.Name == manifest.Name).Select(p => p.Version).ToListAsync(ct);
        if (versions.Contains(manifest.Version)) throw new SolutionException($"{manifest.Name} {manifest.Version} is already published; versions can't change. Bump the version.", StatusCodes.Status409Conflict);
        if (versions.Count > 0 && versions.Max(new VersionComparer()) is { } latest && SolutionValidator.CompareVersions(manifest.Version, latest) < 0)
        {
            throw new SolutionException($"{manifest.Version} is older than the latest published version, {latest}.", StatusCodes.Status409Conflict);
        }

        var publisher = await db.Tenants.AsNoTracking().Where(t => t.TenantId == tenantId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? tenantId;
        var json = JsonSerializer.Serialize(bundle, SolutionManifestReader.Json);
        var digest = Digest(json);
        var now = clock.GetUtcNow();
        if (owner is null) db.SolutionPackageNames.Add(new SolutionPackageNameRecord { Name = manifest.Name, TenantId = tenantId, CreatedAt = now });
        var record = new SolutionPackageRecord
        {
            PackageId = $"{manifest.Name}@{manifest.Version}",
            Name = manifest.Name,
            Version = manifest.Version,
            PublisherTenantId = tenantId,
            PublisherName = publisher,
            Title = manifest.DisplayTitle,
            Description = manifest.Description ?? string.Empty,
            Visibility = visibility,
            BundleJson = json,
            Sha256 = digest,
            Signature = Sign(manifest.Name, manifest.Version, digest),
            PublishedBy = by,
            PublishedAt = now
        };
        db.SolutionPackages.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new SolutionException($"{manifest.Name} {manifest.Version} was just published by someone else, or the name was taken.", StatusCodes.Status409Conflict);
        }

        return View(record, includeWarnings: check.Validation.Warnings);
    }

    private static object View(SolutionPackageRecord p, IReadOnlyList<string>? includeWarnings = null) => new
    {
        package = p.PackageId,
        name = p.Name,
        version = p.Version,
        title = p.Title,
        description = p.Description,
        publisher = p.PublisherName,
        visibility = p.Visibility,
        sha256 = p.Sha256,
        yanked = p.Yanked,
        yank_reason = p.YankReason,
        installs = p.Installs,
        published_at = p.PublishedAt,
        warnings = includeWarnings
    };

    /// <summary>The latest non-yanked version of every package the organization can see, matching <paramref name="query"/>.</summary>
    public async Task<IReadOnlyList<object>> SearchAsync(string tenantId, string? query, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.SolutionPackages.AsNoTracking().Where(p => !p.Yanked && (p.Visibility == "public" || p.PublisherTenantId == tenantId));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = $"%{query.Trim().Replace("%", "").Replace("_", "")}%";
            q = q.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Title, like) || EF.Functions.ILike(p.Description, like));
        }

        var rows = await q.Select(p => new SolutionPackageRecord
        {
            PackageId = p.PackageId, Name = p.Name, Version = p.Version, PublisherTenantId = p.PublisherTenantId, PublisherName = p.PublisherName,
            Title = p.Title, Description = p.Description, Visibility = p.Visibility, Sha256 = p.Sha256, Installs = p.Installs, PublishedAt = p.PublishedAt
        }).ToListAsync(ct);
        // Most installed first (across versions), then by name.
        var installs = rows.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Sum(p => p.Installs));
        return rows.GroupBy(p => p.Name)
            .Select(g => g.OrderByDescending(p => p.Version, new VersionComparer()).First())
            .OrderByDescending(p => installs[p.Name])
            .ThenBy(p => p.Name)
            .Select(p => View(p))
            .ToList();
    }

    /// <summary>Every version of a package (newest first), if the organization can see it.</summary>
    public async Task<IReadOnlyList<object>> VersionsAsync(string tenantId, string name, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SolutionPackages.AsNoTracking().Where(p => p.Name == name).ToListAsync(ct);
        rows = rows.Where(p => VisibleTo(p, tenantId)).ToList();
        if (rows.Count == 0) throw new SolutionException($"No package '{name}'.", StatusCodes.Status404NotFound);
        return rows.OrderByDescending(p => p.Version, new VersionComparer()).Select(p => View(p)).ToList();
    }

    /// <summary>A package version's bundle, checked against its digest and signature: "name",
    /// "name@1.2.0" or "name@latest". Yanked versions only when asked for by exact version.</summary>
    public async Task<(SolutionBundle Bundle, SolutionPackageRecord Package)> ResolveAsync(string tenantId, string reference, CancellationToken ct)
    {
        var at = reference.IndexOf('@');
        var name = at < 0 ? reference.Trim() : reference[..at].Trim();
        var version = at < 0 ? "latest" : reference[(at + 1)..].Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = (await db.SolutionPackages.AsNoTracking().Where(p => p.Name == name).ToListAsync(ct)).Where(p => VisibleTo(p, tenantId)).ToList();
        var package = version == "latest"
            ? candidates.Where(p => !p.Yanked).OrderByDescending(p => p.Version, new VersionComparer()).FirstOrDefault()
            : candidates.FirstOrDefault(p => p.Version == version);
        if (package is null) throw new SolutionException($"No package '{reference}' you can install.", StatusCodes.Status404NotFound);
        if (!Verify(package)) throw new SolutionException($"{package.PackageId} failed its integrity check (it changed after it was published); it can't be installed.", StatusCodes.Status409Conflict);
        var bundle = JsonSerializer.Deserialize<SolutionBundle>(package.BundleJson, SolutionManifestReader.Json) ?? new SolutionBundle();
        return (bundle, package);
    }

    public async Task CountInstallAsync(string packageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SolutionPackages.Where(p => p.PackageId == packageId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Installs, p => p.Installs + 1), ct);
    }

    /// <summary>Yanks a version (no new installs by "latest"; existing installs are unaffected), or un-yanks it.</summary>
    public async Task YankAsync(string tenantId, string name, string version, bool yank, string? reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.SolutionPackages.FirstOrDefaultAsync(p => p.Name == name && p.Version == version, ct);
        if (package is null || !Tenancy.TenantIds.Same(package.PublisherTenantId, tenantId)) throw new SolutionException($"No package {name} {version} of yours.", StatusCodes.Status404NotFound);
        package.Yanked = yank;
        package.YankReason = yank ? reason?.Trim() is { Length: > 0 } r ? r[..Math.Min(500, r.Length)] : null : null;
        await db.SaveChangesAsync(ct);
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) => SolutionValidator.CompareVersions(x ?? "0.0.0", y ?? "0.0.0");
    }
}
