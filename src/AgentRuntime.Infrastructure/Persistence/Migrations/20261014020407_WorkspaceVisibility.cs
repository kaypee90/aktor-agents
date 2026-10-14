using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Workspaces",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "Workspaces",
                type: "text",
                nullable: false,
                defaultValue: "Organization");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_TenantId_Visibility",
                table: "Workspaces",
                columns: new[] { "TenantId", "Visibility" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workspaces_TenantId_Visibility",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "Workspaces");
        }
    }
}
