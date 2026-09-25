using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Provisions the restricted Postgres role that <see cref="DatabaseQueryTool"/> runs agent SQL as
/// (CLAUDE.md section 19/44). The role is a non-superuser that owns only its own schema, so the
/// database enforces what agents can touch: the runtime's history tables and superuser-only
/// functions (pg_read_file, COPY ... PROGRAM, etc.) are simply denied, with no need to parse SQL.
///
/// Each organization gets its own role and schema (docs/platform.md), provisioned on first use, so
/// one tenant's agents can never read or change another's tables; the default tenant keeps the
/// configured role and schema.
/// </summary>
public sealed partial class AgentDatabaseSandbox(IOptions<ToolsOptions> options, ILogger<AgentDatabaseSandbox> logger)
{
    /// <summary>Connection string for the restricted role, or null if provisioning didn't run or failed —
    /// in which case database_query reports itself unavailable rather than falling back to admin.</summary>
    public string? ConnectionString { get; private set; }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _tenantConnections = new();

    /// <summary>The restricted connection for one organization's agents, provisioning its role
    /// and schema the first time. Null if the sandbox is unavailable.</summary>
    public Task<string?> ConnectionStringForAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = AgentRuntime.Tenancy.TenantIds.Normalize(tenantId);
        if (tenant == AgentRuntime.Tenancy.TenantIds.Default) return Task.FromResult(ConnectionString);

        var lazy = _tenantConnections.GetOrAdd(tenant, t => new Lazy<Task<string?>>(() => ProvisionTenantAsync(t)));
        return lazy.Value.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result is not null) return task.Result;
            _tenantConnections.TryRemove(tenant, out _); // retry next time
            return null;
        }, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>"t-1a2b3c4d5e" becomes the suffix "t_1a2b3c4d5e".</summary>
    public static string? TenantSuffix(string tenantId)
    {
        var suffix = new string(tenantId.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return suffix.Length is > 0 and <= 30 ? suffix : null;
    }

    private async Task<string?> ProvisionTenantAsync(string tenantId)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.DatabaseConnectionString) || TenantSuffix(tenantId) is not { } suffix) return null;
        var role = $"{opts.DatabaseAgentRole}_{suffix}";
        var schema = $"{opts.DatabaseAgentSchema}_{suffix}";
        return await ProvisionRoleAsync(role, schema, password: null, CancellationToken.None);
    }

    /// <summary>Drops every table agents created (for an admin reset) and re-provisions the schema empty.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.DatabaseConnectionString) || !IdentifierPattern().IsMatch(opts.DatabaseAgentSchema))
        {
            return;
        }

        await using (var connection = new NpgsqlConnection(opts.DatabaseConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{opts.DatabaseAgentSchema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // Every organization's scratch schema too ({schema}_{tenant}).
            var tenantSchemas = new List<string>();
            await using (var list = new NpgsqlCommand("SELECT nspname FROM pg_namespace WHERE nspname LIKE @prefix", connection))
            {
                list.Parameters.AddWithValue("prefix", opts.DatabaseAgentSchema.Replace("_", "\\_") + "\\_%");
                await using var reader = await list.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) tenantSchemas.Add(reader.GetString(0));
            }

            foreach (var tenantSchema in tenantSchemas.Where(n => IdentifierPattern().IsMatch(n)))
            {
                await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{tenantSchema}\" CASCADE", connection);
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        _tenantConnections.Clear();
        await ProvisionAsync(cancellationToken);
    }

    public async Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.DatabaseConnectionString))
        {
            logger.LogInformation("Tools:DatabaseConnectionString not set; database_query is disabled.");
            return;
        }

        if (!IdentifierPattern().IsMatch(opts.DatabaseAgentRole) || !IdentifierPattern().IsMatch(opts.DatabaseAgentSchema))
        {
            logger.LogError("Tools:DatabaseAgentRole/DatabaseAgentSchema must be lowercase SQL identifiers; database_query is disabled.");
            return;
        }

        ConnectionString = await ProvisionRoleAsync(opts.DatabaseAgentRole, opts.DatabaseAgentSchema,
            string.IsNullOrWhiteSpace(opts.DatabaseAgentPassword) ? null : opts.DatabaseAgentPassword, cancellationToken);
    }

    private async Task<string?> ProvisionRoleAsync(string role, string schema, string? password, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (!IdentifierPattern().IsMatch(role) || !IdentifierPattern().IsMatch(schema))
        {
            logger.LogError("Sandbox role/schema {Role}/{Schema} aren't valid SQL identifiers; database_query is disabled for them.", role, schema);
            return null;
        }

        password ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var timeout = Math.Max(1, opts.DatabaseStatementTimeoutSeconds);

        // Role DDL can't take bind parameters; identifiers are validated above and the password is
        // escaped as a literal. None of these values come from an agent.
        var sql = $"""
            DO $do$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') THEN
                CREATE ROLE "{role}" LOGIN;
              END IF;
            END $do$;
            ALTER ROLE "{role}" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
                PASSWORD '{password.Replace("'", "''")}';
            ALTER ROLE "{role}" SET search_path = "{schema}";
            ALTER ROLE "{role}" SET statement_timeout = '{timeout}s';
            CREATE SCHEMA IF NOT EXISTS "{schema}" AUTHORIZATION "{role}";
            -- The runtime's own tables live in public. Removing PUBLIC's default usage means the agent
            -- role can't even resolve names there (the app's owner/superuser role is unaffected).
            -- Other tenants' schemas are owned by their own roles and never granted to this one.
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            REVOKE ALL ON ALL TABLES IN SCHEMA public FROM "{role}";
            """;

        try
        {
            await using var connection = new NpgsqlConnection(opts.DatabaseConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            logger.LogInformation("database_query sandbox ready: role {Role}, schema {Schema}.", role, schema);
            return new NpgsqlConnectionStringBuilder(opts.DatabaseConnectionString)
            {
                Username = role,
                Password = password,
                SearchPath = schema
            }.ConnectionString;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision the database_query sandbox role {Role}; database_query is disabled for it.", role);
            return null;
        }
    }
}
