using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInteropPreviewsVectorMemoryAndJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BudgetJson",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CallbackAttempts",
                table: "Tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CallbackDeliveredAt",
                table: "Tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CallbackLastError",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CallbackUrl",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EstimateJson",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ForkAfterStep",
                table: "Tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewId",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplayMode",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplayOfTaskId",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Tasks",
                type: "text",
                nullable: false,
                defaultValue: "api");

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "MemoryEntries",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JournalSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    AgentPath = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Step = table.Column<int>(type: "integer", nullable: false),
                    ToolName = table.Column<string>(type: "text", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    InputsReceived = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalSteps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskPreviews",
                columns: table => new
                {
                    PreviewId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Goal = table.Column<string>(type: "text", nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    EstimateJson = table.Column<string>(type: "text", nullable: false),
                    BudgetJson = table.Column<string>(type: "text", nullable: false),
                    PlanningTokens = table.Column<int>(type: "integer", nullable: false),
                    PlanningCostUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TaskId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskPreviews", x => x.PreviewId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CallbackDeliveredAt",
                table: "Tasks",
                column: "CallbackDeliveredAt",
                filter: "\"CallbackUrl\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JournalSteps_TaskId_AgentPath_Kind_Key",
                table: "JournalSteps",
                columns: new[] { "TaskId", "AgentPath", "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalSteps_TenantId_TaskId_Id",
                table: "JournalSteps",
                columns: new[] { "TenantId", "TaskId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskPreviews_TenantId_CreatedAt",
                table: "TaskPreviews",
                columns: new[] { "TenantId", "CreatedAt" });

            // Semantic memory (docs/memory.md): pgvector, where the server has it. A plain Postgres
            // keeps working without the column; memory search then uses keywords only.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
                        CREATE EXTENSION IF NOT EXISTS vector;
                        ALTER TABLE "MemoryEntries" ADD COLUMN IF NOT EXISTS "Embedding" vector;
                    END IF;
                END $$;
                """);

            // Keyword half of hybrid search.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_MemoryEntries_FullText"
                    ON "MemoryEntries" USING gin (to_tsvector('english', "Key" || ' ' || "Value"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_MemoryEntries_FullText";""");
            migrationBuilder.Sql("""ALTER TABLE "MemoryEntries" DROP COLUMN IF EXISTS "Embedding";""");

            migrationBuilder.DropTable(
                name: "JournalSteps");

            migrationBuilder.DropTable(
                name: "TaskPreviews");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_CallbackDeliveredAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "BudgetJson",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CallbackAttempts",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CallbackDeliveredAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CallbackLastError",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CallbackUrl",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "EstimateJson",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ForkAfterStep",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "PreviewId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ReplayMode",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ReplayOfTaskId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "MemoryEntries");
        }
    }
}
