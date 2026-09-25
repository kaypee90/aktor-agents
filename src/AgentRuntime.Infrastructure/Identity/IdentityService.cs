using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Identity;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>"accounts" (sign-in required) or "disabled" (single-user: everyone is the owner of
    /// the default organization; only for a machine nobody else can reach).</summary>
    public string Mode { get; set; } = "accounts";

    public bool Disabled => string.Equals(Mode, "disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Anyone can create an account (and their own organization). Off: only invitations.
    /// The very first account can always be created.</summary>
    public bool AllowSignup { get; set; } = true;

    /// <summary>Emails of the operators, who may reset the server and change any organization's plan.</summary>
    public List<string> PlatformAdmins { get; set; } = [];

    public int SessionDays { get; set; } = 30;
    public int InvitationDays { get; set; } = 7;
    public string CookieName { get; set; } = "aktor_session";
}

/// <summary>Who is making a request, as the API sees it.</summary>
public sealed record Caller(string TenantId, TenantRole Role, string? UserId, string? Email, string? ApiKeyId, bool PlatformAdmin, string? SessionHash)
{
    public bool IsApiKey => ApiKeyId is not null;
    public string ActorId => UserId ?? $"key:{ApiKeyId}";
}

public sealed record IdentityResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
    public static IdentityResult<T> Ok(T value) => new(value, null);
    public static IdentityResult<T> Fail(string error) => new(default, error);
}

public sealed record SignedIn(string SessionToken, UserRecord User, string TenantId);

public sealed record MembershipView(string TenantId, string TenantName, TenantRole Role);

public sealed record MemberView(string UserId, string Email, string Name, TenantRole Role, DateTimeOffset JoinedAt);

public sealed record ApiKeyView(string KeyId, string Name, string Display, TenantRole Role, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);

public sealed record InvitationView(string InvitationId, string Email, TenantRole Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt);

/// <summary>
/// Accounts, organizations, memberships, sessions, invitations and API keys (docs/platform.md).
/// Secrets (passwords, session tokens, keys, invitation links) are only ever stored hashed.
/// </summary>
public sealed class IdentityService(
    IDbContextFactory<AgentDbContext> dbFactory,
    IOptions<AuthOptions> authOptions,
    IOptions<BillingOptions> billingOptions,
    IGrainFactory grains,
    ILogger<IdentityService> logger)
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);
    // Verified against when an email is unknown, so a login takes as long either way.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash(SecretTokens.New()));

    private AuthOptions Auth => authOptions.Value;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool LooksLikeEmail(string email) =>
        email.Length is > 3 and <= 254 && email.IndexOf('@') is > 0 and var at && at < email.Length - 1 && !email.Contains(' ');

    public bool IsPlatformAdmin(string? email) =>
        Auth.Disabled || (email is not null && Auth.PlatformAdmins.Any(a => NormalizeEmail(a) == email));

    // ---- Accounts ---------------------------------------------------------------

    /// <summary>Creates an account. With an invitation it joins that organization; otherwise it
    /// gets a new one of its own. The very first account also takes over the default organization,
    /// which holds everything created before accounts existed.</summary>
    public async Task<IdentityResult<SignedIn>> SignUpAsync(string email, string password, string? name, string? organizationName,
        string? invitationToken, CancellationToken ct)
    {
        email = NormalizeEmail(email ?? string.Empty);
        if (!LooksLikeEmail(email)) return IdentityResult<SignedIn>.Fail("Enter a valid email address.");
        if (PasswordHasher.Validate(password) is { } weak) return IdentityResult<SignedIn>.Fail(weak);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return IdentityResult<SignedIn>.Fail("An account with this email already exists. Sign in instead.");

        var firstAccount = !await db.Users.AnyAsync(ct);
        InvitationRecord? invitation = null;
        if (!string.IsNullOrWhiteSpace(invitationToken))
        {
            invitation = await FindOpenInvitationAsync(db, invitationToken, ct);
            if (invitation is null) return IdentityResult<SignedIn>.Fail("This invitation is invalid or has expired.");
            if (invitation.Email != email) return IdentityResult<SignedIn>.Fail("This invitation was sent to a different email address.");
        }
        else if (!Auth.AllowSignup && !firstAccount)
        {
            return IdentityResult<SignedIn>.Fail("Sign-up is by invitation only on this server.");
        }

        var now = DateTimeOffset.UtcNow;
        var user = new UserRecord
        {
            UserId = "u-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            Email = email,
            Name = Clip(name, 100) ?? email.Split('@')[0],
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = now,
            LastLoginAt = now
        };
        db.Users.Add(user);

        string tenantId;
        if (invitation is not null)
        {
            if (await MemberLimitReachedAsync(db, invitation.TenantId, ct)) return IdentityResult<SignedIn>.Fail("The organization has no seats left on its plan.");
            tenantId = invitation.TenantId;
            invitation.AcceptedAt = now;
            db.Memberships.Add(new MembershipRecord { TenantId = tenantId, UserId = user.UserId, Role = invitation.Role, CreatedAt = now });
        }
        else
        {
            var orgName = Clip(organizationName, 100) ?? $"{user.Name}'s organization";
            if (firstAccount && await db.Tenants.FindAsync([TenantIds.Default], ct) is null)
            {
                tenantId = TenantIds.Default;
            }
            else
            {
                tenantId = TenantIds.New();
            }

            db.Tenants.Add(new TenantRecord { TenantId = tenantId, Name = orgName, CreatedAt = now, PlanId = billingOptions.Value.DefaultPlan });
            db.Memberships.Add(new MembershipRecord { TenantId = tenantId, UserId = user.UserId, Role = nameof(TenantRole.Owner), CreatedAt = now });
        }

        var token = NewSession(db, user.UserId, tenantId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Account {UserId} created in tenant {TenantId}", user.UserId, tenantId);
        return IdentityResult<SignedIn>.Ok(new SignedIn(token, user, tenantId));
    }

    public async Task<IdentityResult<SignedIn>> SignInAsync(string email, string password, CancellationToken ct)
    {
        email = NormalizeEmail(email ?? string.Empty);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            PasswordHasher.Verify(password ?? string.Empty, DummyHash.Value);
            return IdentityResult<SignedIn>.Fail("Wrong email or password.");
        }

        if (!PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash)) return IdentityResult<SignedIn>.Fail("Wrong email or password.");

        var membership = await db.Memberships.Where(m => m.UserId == user.UserId).OrderBy(m => m.CreatedAt).FirstOrDefaultAsync(ct);
        if (membership is null) return IdentityResult<SignedIn>.Fail("This account doesn't belong to any organization any more.");

        if (PasswordHasher.NeedsRehash(user.PasswordHash)) user.PasswordHash = PasswordHasher.Hash(password!);
        var now = DateTimeOffset.UtcNow;
        user.LastLoginAt = now;
        var token = NewSession(db, user.UserId, membership.TenantId, now);
        await db.SaveChangesAsync(ct);
        return IdentityResult<SignedIn>.Ok(new SignedIn(token, user, membership.TenantId));
    }

    private string NewSession(AgentDbContext db, string userId, string tenantId, DateTimeOffset now)
    {
        var token = "s_" + SecretTokens.New();
        db.Sessions.Add(new SessionRecord
        {
            TokenHash = SecretTokens.Hash(token),
            UserId = userId,
            TenantId = tenantId,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddDays(Math.Max(1, Auth.SessionDays))
        });
        return token;
    }

    /// <summary>Resolves a session cookie, sliding its expiry. Null if it's unknown, expired, or the
    /// user no longer belongs to the organization it was working in.</summary>
    public async Task<Caller?> ResolveSessionAsync(string token, CancellationToken ct)
    {
        if (!token.StartsWith("s_", StringComparison.Ordinal)) return null;
        var hash = SecretTokens.Hash(token);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.FindAsync([hash], ct);
        var now = DateTimeOffset.UtcNow;
        if (session is null || session.ExpiresAt <= now) return null;

        var membership = await db.Memberships.FindAsync([session.TenantId, session.UserId], ct);
        var user = await db.Users.FindAsync([session.UserId], ct);
        if (membership is null || user is null) return null;

        if (now - session.LastSeenAt > TouchInterval)
        {
            session.LastSeenAt = now;
            session.ExpiresAt = now.AddDays(Math.Max(1, Auth.SessionDays));
            await db.SaveChangesAsync(ct);
        }

        return new Caller(session.TenantId, ParseRole(membership.Role), user.UserId, user.Email, null, IsPlatformAdmin(user.Email), hash);
    }

    public async Task SignOutAsync(string sessionHash, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Sessions.Where(s => s.TokenHash == sessionHash).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> SwitchTenantAsync(string sessionHash, string userId, string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Memberships.FindAsync([tenantId, userId], ct) is null) return false;
        var session = await db.Sessions.FindAsync([sessionHash], ct);
        if (session is null || session.UserId != userId) return false;
        session.TenantId = tenantId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IdentityResult<bool>> ChangePasswordAsync(string userId, string currentPassword, string newPassword, string keepSessionHash, CancellationToken ct)
    {
        if (PasswordHasher.Validate(newPassword) is { } weak) return IdentityResult<bool>.Fail(weak);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null || !PasswordHasher.Verify(currentPassword, user.PasswordHash)) return IdentityResult<bool>.Fail("The current password is wrong.");
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await db.SaveChangesAsync(ct);
        // Every other browser has to sign in again.
        await db.Sessions.Where(s => s.UserId == userId && s.TokenHash != keepSessionHash).ExecuteDeleteAsync(ct);
        return IdentityResult<bool>.Ok(true);
    }

    public async Task<IReadOnlyList<MembershipView>> MembershipsAsync(string userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Memberships.Where(m => m.UserId == userId)
            .Join(db.Tenants, m => m.TenantId, t => t.TenantId, (m, t) => new { m, t })
            .OrderBy(x => x.m.CreatedAt)
            .Select(x => new MembershipView(x.t.TenantId, x.t.Name, ParseRole(x.m.Role)))
            .ToListAsync(ct);
    }

    // ---- Organizations and members --------------------------------------------------

    public async Task<TenantRecord?> GetTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
    }

    public async Task RenameTenantAsync(string tenantId, string name, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null || Clip(name, 100) is not { } clipped) return;
        tenant.Name = clipped;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MemberView>> MembersAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Memberships.Where(m => m.TenantId == tenantId)
            .Join(db.Users, m => m.UserId, u => u.UserId, (m, u) => new { m, u })
            .OrderBy(x => x.m.CreatedAt)
            .Select(x => new MemberView(x.u.UserId, x.u.Email, x.u.Name, ParseRole(x.m.Role), x.m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IdentityResult<bool>> SetRoleAsync(Caller caller, string userId, TenantRole role, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var membership = await db.Memberships.FindAsync([caller.TenantId, userId], ct);
        if (membership is null) return IdentityResult<bool>.Fail("No such member.");
        var current = ParseRole(membership.Role);
        // Only owners make or unmake owners; admins manage everyone below them.
        if ((role == TenantRole.Owner || current == TenantRole.Owner) && caller.Role != TenantRole.Owner)
        {
            return IdentityResult<bool>.Fail("Only an owner can change who is an owner.");
        }

        if (current == TenantRole.Owner && role != TenantRole.Owner && await OwnerCountAsync(db, caller.TenantId, ct) <= 1)
        {
            return IdentityResult<bool>.Fail("An organization needs at least one owner.");
        }

        membership.Role = role.ToString();
        await db.SaveChangesAsync(ct);
        return IdentityResult<bool>.Ok(true);
    }

    public async Task<IdentityResult<bool>> RemoveMemberAsync(Caller caller, string userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var membership = await db.Memberships.FindAsync([caller.TenantId, userId], ct);
        if (membership is null) return IdentityResult<bool>.Fail("No such member.");
        var role = ParseRole(membership.Role);
        if (role == TenantRole.Owner && caller.Role != TenantRole.Owner && caller.UserId != userId)
        {
            return IdentityResult<bool>.Fail("Only an owner can remove an owner.");
        }

        if (role == TenantRole.Owner && await OwnerCountAsync(db, caller.TenantId, ct) <= 1)
        {
            return IdentityResult<bool>.Fail("An organization needs at least one owner.");
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        await db.Sessions.Where(s => s.UserId == userId && s.TenantId == caller.TenantId).ExecuteDeleteAsync(ct);
        return IdentityResult<bool>.Ok(true);
    }

    private static Task<int> OwnerCountAsync(AgentDbContext db, string tenantId, CancellationToken ct) =>
        db.Memberships.CountAsync(m => m.TenantId == tenantId && m.Role == nameof(TenantRole.Owner), ct);

    private async Task<bool> MemberLimitReachedAsync(AgentDbContext db, string tenantId, CancellationToken ct)
    {
        var plan = await grains.GetGrain<ITenantGrain>(tenantId).GetPlan();
        return plan.MaxMembers > 0 && await db.Memberships.CountAsync(m => m.TenantId == tenantId, ct) >= plan.MaxMembers;
    }

    // ---- Invitations --------------------------------------------------------------

    /// <summary>Creates an invitation and returns its one-time token (for the invitation link).</summary>
    public async Task<IdentityResult<(InvitationView Invitation, string Token)>> InviteAsync(Caller caller, string email, TenantRole role, CancellationToken ct)
    {
        email = NormalizeEmail(email ?? string.Empty);
        if (!LooksLikeEmail(email)) return IdentityResult<(InvitationView, string)>.Fail("Enter a valid email address.");
        if (role > caller.Role) return IdentityResult<(InvitationView, string)>.Fail("You can't invite someone with more access than you have.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Memberships.Join(db.Users, m => m.UserId, u => u.UserId, (m, u) => new { m, u })
                .AnyAsync(x => x.m.TenantId == caller.TenantId && x.u.Email == email, ct))
        {
            return IdentityResult<(InvitationView, string)>.Fail("They're already a member.");
        }

        if (await MemberLimitReachedAsync(db, caller.TenantId, ct))
        {
            return IdentityResult<(InvitationView, string)>.Fail("The organization has no seats left on its plan.");
        }

        var token = "inv_" + SecretTokens.New();
        var now = DateTimeOffset.UtcNow;
        var invitation = new InvitationRecord
        {
            InvitationId = "inv-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            TenantId = caller.TenantId,
            Email = email,
            Role = role.ToString(),
            TokenHash = SecretTokens.Hash(token),
            InvitedByUserId = caller.ActorId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Max(1, Auth.InvitationDays))
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        return IdentityResult<(InvitationView, string)>.Ok((ToView(invitation), token));
    }

    public async Task<IReadOnlyList<InvitationView>> InvitationsAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Invitations.AsNoTracking().Where(i => i.TenantId == tenantId).OrderByDescending(i => i.CreatedAt).Take(100).ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    public async Task<bool> RevokeInvitationAsync(string tenantId, string invitationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.TenantId == tenantId && i.InvitationId == invitationId, ct);
        if (invitation is null || invitation.AcceptedAt is not null) return false;
        invitation.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>What an invitation link is for, so the sign-up page can show it. Null if it's not usable.</summary>
    public async Task<(string TenantName, string Email, TenantRole Role)?> PeekInvitationAsync(string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var invitation = await FindOpenInvitationAsync(db, token, ct);
        if (invitation is null) return null;
        var tenant = await db.Tenants.FindAsync([invitation.TenantId], ct);
        return (tenant?.Name ?? "an organization", invitation.Email, ParseRole(invitation.Role));
    }

    /// <summary>A signed-in user accepting an invitation sent to their email.</summary>
    public async Task<IdentityResult<string>> AcceptInvitationAsync(string userId, string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var invitation = await FindOpenInvitationAsync(db, token, ct);
        var user = await db.Users.FindAsync([userId], ct);
        if (invitation is null || user is null) return IdentityResult<string>.Fail("This invitation is invalid or has expired.");
        if (invitation.Email != user.Email) return IdentityResult<string>.Fail("This invitation was sent to a different email address.");

        if (await db.Memberships.FindAsync([invitation.TenantId, userId], ct) is null)
        {
            if (await MemberLimitReachedAsync(db, invitation.TenantId, ct)) return IdentityResult<string>.Fail("The organization has no seats left on its plan.");
            db.Memberships.Add(new MembershipRecord { TenantId = invitation.TenantId, UserId = userId, Role = invitation.Role, CreatedAt = DateTimeOffset.UtcNow });
        }

        invitation.AcceptedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return IdentityResult<string>.Ok(invitation.TenantId);
    }

    private static async Task<InvitationRecord?> FindOpenInvitationAsync(AgentDbContext db, string token, CancellationToken ct)
    {
        var hash = SecretTokens.Hash(token.Trim());
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        return invitation is { AcceptedAt: null, RevokedAt: null } && invitation.ExpiresAt > DateTimeOffset.UtcNow ? invitation : null;
    }

    private static InvitationView ToView(InvitationRecord i) =>
        new(i.InvitationId, i.Email, ParseRole(i.Role), i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt);

    // ---- API keys ---------------------------------------------------------------------

    public async Task<IdentityResult<(ApiKeyView Key, string Secret)>> CreateApiKeyAsync(Caller caller, string name, TenantRole role, int? expiresInDays, CancellationToken ct)
    {
        if (role > caller.Role) return IdentityResult<(ApiKeyView, string)>.Fail("A key can't have more access than you have.");
        if (role == TenantRole.Owner) return IdentityResult<(ApiKeyView, string)>.Fail("Keys can be at most Admin: owner actions need a person.");

        var (keyId, fullKey) = ApiKeyFormat.New();
        var now = DateTimeOffset.UtcNow;
        var record = new ApiKeyRecord
        {
            KeyId = keyId,
            TenantId = caller.TenantId,
            Name = Clip(name, 100) ?? "API key",
            Prefix = ApiKeyFormat.Display(fullKey),
            SecretHash = SecretTokens.Hash(fullKey),
            Role = role.ToString(),
            CreatedByUserId = caller.ActorId,
            CreatedAt = now,
            ExpiresAt = expiresInDays is > 0 ? now.AddDays(expiresInDays.Value) : null
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiKeys.Add(record);
        await db.SaveChangesAsync(ct);
        return IdentityResult<(ApiKeyView, string)>.Ok((ToView(record), fullKey));
    }

    public async Task<IReadOnlyList<ApiKeyView>> ApiKeysAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ApiKeys.AsNoTracking().Where(k => k.TenantId == tenantId).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    public async Task<bool> RevokeApiKeyAsync(string tenantId, string keyId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.TenantId == tenantId && k.KeyId == keyId, ct);
        if (key is null) return false;
        key.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Caller?> ResolveApiKeyAsync(string presented, CancellationToken ct)
    {
        if (!ApiKeyFormat.TryParse(presented, out var keyId)) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FindAsync([keyId], ct);
        var now = DateTimeOffset.UtcNow;
        if (key is null || key.RevokedAt is not null || key.ExpiresAt <= now || !SecretTokens.HashEquals(presented, key.SecretHash)) return null;

        if (key.LastUsedAt is null || now - key.LastUsedAt > TouchInterval)
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new Caller(key.TenantId, ParseRole(key.Role), null, null, key.KeyId, false, null);
    }

    private static ApiKeyView ToView(ApiKeyRecord k) =>
        new(k.KeyId, k.Name, k.Prefix, ParseRole(k.Role), k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.RevokedAt);

    // ---- Helpers ------------------------------------------------------------------------

    public static TenantRole ParseRole(string role) => Enum.TryParse<TenantRole>(role, ignoreCase: true, out var r) ? r : TenantRole.Viewer;

    private static string? Clip(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }
}
