using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Npgsql;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Runs agent SQL as the restricted role provisioned by <see cref="AgentDatabaseSandbox"/>, in its
/// own scratch schema. Isolation is enforced by Postgres privileges, not by inspecting the SQL: the
/// role can't see the runtime's history tables or call superuser-only functions. Reads additionally
/// run in a READ ONLY transaction, so DatabaseRead can never be used to modify data. The connection
/// string is held by the runtime and never exposed to the agent (CLAUDE.md section 44).
/// </summary>
public sealed partial class DatabaseQueryTool(AgentDatabaseSandbox sandbox) : ITool
{
    private const int MaxRows = 200;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "database_query",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Run one SQL statement in your private Postgres scratch schema. Read access allows " +
                      "SELECT/WITH queries; write access also allows CREATE/ALTER/DROP TABLE and " +
                      "INSERT/UPDATE/DELETE. You can only see tables you (or other agents) created there.",
        RequiredPermissions = ToolPermission.DatabaseRead,
        JsonSchema = """{ "type": "object", "properties": { "sql": { "type": "string" } }, "required": ["sql"] }"""
    };

    [GeneratedRegex(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReadPattern();

    [GeneratedRegex(@"^\s*(INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE|DROP\s+TABLE|CREATE\s+INDEX)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WritePattern();

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<SqlArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid database_query arguments.");

        if (await sandbox.ConnectionStringForAsync(request.TenantId, request.CancellationToken) is not { } connectionString)
        {
            return ToolExecutionResult.Fail("database_query is not available (the sandbox database role isn't configured).");
        }

        var sql = args.Sql.Trim().TrimEnd(';').TrimEnd();

        // One statement per call keeps the read/write classification below meaningful — Npgsql
        // would otherwise run every statement in "SELECT 1; DELETE ...".
        if (sql.Contains(';'))
        {
            return ToolExecutionResult.Fail("database_query accepts a single SQL statement only (no ';').");
        }

        var isRead = ReadPattern().IsMatch(sql);
        var isWrite = WritePattern().IsMatch(sql);
        if (!isRead && !isWrite)
        {
            return ToolExecutionResult.Fail(
                "Only SELECT/WITH, INSERT/UPDATE/DELETE, and CREATE/ALTER/DROP TABLE or CREATE INDEX are allowed.");
        }

        if (isWrite && !request.GrantedPermissions.HasFlag(ToolPermission.DatabaseWrite))
        {
            return ToolExecutionResult.Fail("Write statements require the DatabaseWrite permission, which this agent was not granted.");
        }

        var ct = request.CancellationToken;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            if (isRead)
            {
                await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                {
                    await readOnly.ExecuteNonQueryAsync(ct);
                }

                var rows = new List<Dictionary<string, object?>>();
                await using (var command = new NpgsqlCommand(sql, connection, transaction))
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (rows.Count < MaxRows && await reader.ReadAsync(ct))
                    {
                        var row = new Dictionary<string, object?>();
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }

                        rows.Add(row);
                    }
                }

                await transaction.CommitAsync(ct);
                return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { rows, truncated = rows.Count == MaxRows }, ToolJson.Options));
            }

            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                var affected = await command.ExecuteNonQueryAsync(ct);
                await transaction.CommitAsync(ct);
                return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { rows_affected = affected }, ToolJson.Options));
            }
        }
        catch (PostgresException ex)
        {
            // Permission errors, syntax errors, and statement timeouts are normal feedback for the
            // agent, not runtime faults.
            return ToolExecutionResult.Fail($"SQL error {ex.SqlState}: {ex.MessageText}");
        }
    }

    private sealed record SqlArgs(string Sql);
}
