using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddToolCallDurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "ToolCalls",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolCalls_TaskId",
                table: "ToolCalls",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ToolCalls_TaskId",
                table: "ToolCalls");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "ToolCalls");
        }
    }
}
