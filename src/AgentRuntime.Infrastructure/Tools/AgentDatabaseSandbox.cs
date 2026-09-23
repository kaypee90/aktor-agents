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
/// </summary>
public sealed partial class AgentDatabaseSandbox(IOptions<ToolsOptions> options, ILogger<AgentDatabaseSandbox> logger)
{
    /// <summary>Connection string for the restricted role, or null if provisioning didn't run or failed —
    /// in which case database_query reports itself unavailable rather than falling back to admin.</summary>
    public string? ConnectionString { get; private set; }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();

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
        }

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

        var role = opts.DatabaseAgentRole;
        var schema = opts.DatabaseAgentSchema;
        var password = string.IsNullOrWhiteSpace(opts.DatabaseAgentPassword)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24))
            : opts.DatabaseAgentPassword;
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
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            REVOKE ALL ON ALL TABLES IN SCHEMA public FROM "{role}";
            """;

        try
        {
            await using var connection = new NpgsqlConnection(opts.DatabaseConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            ConnectionString = new NpgsqlConnectionStringBuilder(opts.DatabaseConnectionString)
            {
                Username = role,
                Password = password,
                SearchPath = schema
            }.ConnectionString;

            logger.LogInformation("database_query sandbox ready: role {Role}, schema {Schema}.", role, schema);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision the database_query sandbox role; database_query is disabled.");
        }
    }
}
