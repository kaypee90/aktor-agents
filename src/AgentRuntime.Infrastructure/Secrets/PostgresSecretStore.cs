using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Integrations;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Secrets;

/// <summary>Connection secrets, encrypted with <see cref="SecretProtector"/> before they reach the
/// database. The agent sandbox role (database_query) has no access to this table.</summary>
public sealed class PostgresSecretStore(IDbContextFactory<AgentDbContext> dbFactory, SecretProtector protector) : ISecretStore
{
    public async Task PutAsync(string scope, string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cipher = protector.Protect(value, scope, key);
        var existing = await db.Secrets.FindAsync([scope, key], cancellationToken);
        if (existing is null)
        {
            db.Secrets.Add(new SecretRecord { Scope = scope, Key = key, Ciphertext = cipher, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            existing.Ciphertext = cipher;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Scope == scope && s.Key == key, cancellationToken);
        return row is null ? null : protector.Unprotect(row.Ciphertext, scope, key);
    }

    public async Task DeleteScopeAsync(string scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Secrets.Where(s => s.Scope == scope).ExecuteDeleteAsync(cancellationToken);
    }
}
