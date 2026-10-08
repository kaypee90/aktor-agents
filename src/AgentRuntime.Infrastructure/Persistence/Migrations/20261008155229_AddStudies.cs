using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStudies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Workspaces",
                type: "text",
                nullable: false,
                defaultValue: "pipeline");

            migrationBuilder.CreateTable(
                name: "Studies",
                columns: table => new
                {
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Question = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LastRunId = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Studies", x => x.StudyId);
                });

            migrationBuilder.CreateTable(
                name: "StudyDatasets",
                columns: table => new
                {
                    DatasetId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Rows = table.Column<long>(type: "bigint", nullable: false),
                    TrainRows = table.Column<long>(type: "bigint", nullable: false),
                    HoldoutRows = table.Column<long>(type: "bigint", nullable: false),
                    TimeColumn = table.Column<string>(type: "text", nullable: true),
                    HoldoutFraction = table.Column<double>(type: "double precision", nullable: false),
                    ProfileJson = table.Column<string>(type: "text", nullable: false),
                    DictionaryJson = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Current = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyDatasets", x => x.DatasetId);
                });

            migrationBuilder.CreateTable(
                name: "StudyEvidence",
                columns: table => new
                {
                    EvidenceId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    SourceKey = table.Column<string>(type: "text", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    DetailJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyEvidence", x => x.EvidenceId);
                });

            migrationBuilder.CreateTable(
                name: "StudyHypotheses",
                columns: table => new
                {
                    HypothesisId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    Statement = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyHypotheses", x => x.HypothesisId);
                });

            migrationBuilder.CreateTable(
                name: "StudyModels",
                columns: table => new
                {
                    ModelId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    HypothesisId = table.Column<string>(type: "text", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: false),
                    DatasetId = table.Column<string>(type: "text", nullable: false),
                    DatasetName = table.Column<string>(type: "text", nullable: false),
                    DatasetVersion = table.Column<int>(type: "integer", nullable: false),
                    Target = table.Column<string>(type: "text", nullable: false),
                    FeaturesJson = table.Column<string>(type: "text", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReviewerAgentId = table.Column<string>(type: "text", nullable: true),
                    ReviewNotes = table.Column<string>(type: "text", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HoldoutJson = table.Column<string>(type: "text", nullable: true),
                    HoldoutEvidenceId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyModels", x => x.ModelId);
                });

            migrationBuilder.CreateTable(
                name: "StudyReports",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyReports", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "StudySimulations",
                columns: table => new
                {
                    SimulationId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SpecJson = table.Column<string>(type: "text", nullable: false),
                    SummaryJson = table.Column<string>(type: "text", nullable: false),
                    DatasetName = table.Column<string>(type: "text", nullable: false),
                    EvidenceId = table.Column<string>(type: "text", nullable: false),
                    Participants = table.Column<int>(type: "integer", nullable: false),
                    Decisions = table.Column<int>(type: "integer", nullable: false),
                    Tokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudySimulations", x => x.SimulationId);
                });

            migrationBuilder.CreateTable(
                name: "StudySourceRoles",
                columns: table => new
                {
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    SourceKey = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudySourceRoles", x => new { x.StudyId, x.SourceKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Studies_TenantId_CreatedAt",
                table: "Studies",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Studies_WorkspaceId",
                table: "Studies",
                column: "WorkspaceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudyDatasets_StudyId_Name_Version",
                table: "StudyDatasets",
                columns: new[] { "StudyId", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudyEvidence_StudyId_CreatedAt",
                table: "StudyEvidence",
                columns: new[] { "StudyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StudyHypotheses_StudyId",
                table: "StudyHypotheses",
                column: "StudyId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyModels_StudyId",
                table: "StudyModels",
                column: "StudyId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyReports_StudyId",
                table: "StudyReports",
                column: "StudyId");

            migrationBuilder.CreateIndex(
                name: "IX_StudySimulations_StudyId",
                table: "StudySimulations",
                column: "StudyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Studies");

            migrationBuilder.DropTable(
                name: "StudyDatasets");

            migrationBuilder.DropTable(
                name: "StudyEvidence");

            migrationBuilder.DropTable(
                name: "StudyHypotheses");

            migrationBuilder.DropTable(
                name: "StudyModels");

            migrationBuilder.DropTable(
                name: "StudyReports");

            migrationBuilder.DropTable(
                name: "StudySimulations");

            migrationBuilder.DropTable(
                name: "StudySourceRoles");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Workspaces");
        }
    }
}
