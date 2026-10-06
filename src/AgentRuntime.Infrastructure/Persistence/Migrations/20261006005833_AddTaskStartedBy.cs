using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskStartedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartedBy",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantId_StartedBy",
                table: "Tasks",
                columns: new[] { "TenantId", "StartedBy" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_TenantId_StartedBy",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "StartedBy",
                table: "Tasks");
        }
    }
}
