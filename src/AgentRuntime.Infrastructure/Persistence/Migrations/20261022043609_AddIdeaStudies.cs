using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdeaStudies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdeaJson",
                table: "Studies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Studies",
                type: "text",
                nullable: false,
                defaultValue: "question");

            migrationBuilder.CreateTable(
                name: "StudyAssumptions",
                columns: table => new
                {
                    AssumptionId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    Low = table.Column<double>(type: "double precision", nullable: false),
                    Likely = table.Column<double>(type: "double precision", nullable: false),
                    High = table.Column<double>(type: "double precision", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReviewerAgentId = table.Column<string>(type: "text", nullable: true),
                    ReviewNotes = table.Column<string>(type: "text", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Current = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyAssumptions", x => x.AssumptionId);
                });

            migrationBuilder.CreateTable(
                name: "StudyProjections",
                columns: table => new
                {
                    ProjectionId = table.Column<string>(type: "text", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SpecJson = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyProjections", x => x.ProjectionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudyAssumptions_StudyId_Name_Current",
                table: "StudyAssumptions",
                columns: new[] { "StudyId", "Name", "Current" });

            migrationBuilder.CreateIndex(
                name: "IX_StudyProjections_StudyId",
                table: "StudyProjections",
                column: "StudyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudyAssumptions");

            migrationBuilder.DropTable(
                name: "StudyProjections");

            migrationBuilder.DropColumn(
                name: "IdeaJson",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Studies");
        }
    }
}
