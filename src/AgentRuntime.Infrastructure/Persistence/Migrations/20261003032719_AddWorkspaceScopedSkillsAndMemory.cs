using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceScopedSkillsAndMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Skills",
                table: "Skills");

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "Skills",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "MemoryEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Skills",
                table: "Skills",
                columns: new[] { "TenantId", "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEntries_TenantId_WorkspaceId",
                table: "MemoryEntries",
                columns: new[] { "TenantId", "WorkspaceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Skills",
                table: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_MemoryEntries_TenantId_WorkspaceId",
                table: "MemoryEntries");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "MemoryEntries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Skills",
                table: "Skills",
                columns: new[] { "TenantId", "Name" });
        }
    }
}
