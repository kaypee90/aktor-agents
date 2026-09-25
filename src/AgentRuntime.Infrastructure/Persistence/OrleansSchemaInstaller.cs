using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Installs Orleans' PostgreSQL schema (the official scripts from dotnet/orleans, embedded in this
/// assembly) the first time the runtime starts against a database, so durable grain state and
/// reminders work with no manual setup. Each script is applied only if its table is missing — the
/// upstream scripts aren't re-runnable — and inside a transaction, so a crash can't leave half a
/// schema behind.
/// </summary>
public sealed class OrleansSchemaInstaller(ILogger<OrleansSchemaInstaller> logger)
{
    private static readonly (string Script, string Table)[] Core =
    [
        ("PostgreSQL-Main.sql", "orleansquery"),
        ("PostgreSQL-Persistence.sql", "orleansstorage"),
        ("PostgreSQL-Reminders.sql", "orleansreminderstable")
    ];

    private static readonly (string Script, string Table) Clustering = ("PostgreSQL-Clustering.sql", "orleansmembershiptable");

    public async Task InstallAsync(string connectionString, bool includeClustering, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var scripts = includeClustering ? [.. Core, Clustering] : Core;
        foreach (var (script, table) in scripts)
        {
            if (await TableExistsAsync(connection, table, cancellationToken)) continue;

            logger.LogInformation("Installing Orleans schema {Script}", script);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand(ReadScript(script), connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("name", $"public.{table}");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string ReadScript(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
