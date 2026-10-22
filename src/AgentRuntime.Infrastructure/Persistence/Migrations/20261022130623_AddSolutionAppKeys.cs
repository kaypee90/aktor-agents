using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutionAppKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolutionAppKeys",
                columns: table => new
                {
                    KeyId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    InstallId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: false),
                    SecretHash = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionAppKeys", x => x.KeyId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionAppKeys_TenantId_InstallId",
                table: "SolutionAppKeys",
                columns: new[] { "TenantId", "InstallId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolutionAppKeys");
        }
    }
}
