using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlobalConnectorLinks",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    ConnectionId = table.Column<string>(type: "text", nullable: false),
                    ConnectorId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    OwnerKind = table.Column<string>(type: "text", nullable: false),
                    ConnectionName = table.Column<string>(type: "text", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalConnectorLinks", x => new { x.OwnerId, x.ConnectionId });
                });

            migrationBuilder.CreateTable(
                name: "GlobalConnectors",
                columns: table => new
                {
                    ConnectorId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PluginId = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    SecretKeysJson = table.Column<string>(type: "text", nullable: false),
                    UsableInTasks = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalConnectors", x => x.ConnectorId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlobalConnectorLinks_TenantId_ConnectorId",
                table: "GlobalConnectorLinks",
                columns: new[] { "TenantId", "ConnectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_GlobalConnectors_TenantId_Name",
                table: "GlobalConnectors",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlobalConnectorLinks");

            migrationBuilder.DropTable(
                name: "GlobalConnectors");
        }
    }
}
