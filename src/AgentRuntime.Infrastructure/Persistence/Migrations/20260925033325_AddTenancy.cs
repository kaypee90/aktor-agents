using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Worlds_CreatedAt",
                table: "Worlds");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_CreatedAt",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_MemoryEntries_AgentId_Key",
                table: "MemoryEntries");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Worlds",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Workspaces",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "ToolCalls",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Tasks",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Messages",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "MemoryEntries",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Events",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Artifacts",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Agents",
                type: "text",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    KeyId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: false),
                    SecretHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.KeyId);
                });

            migrationBuilder.CreateTable(
                name: "BillingEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    InvitationId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    InvitedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.InvitationId);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.TenantId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PlanId = table.Column<string>(type: "text", nullable: false),
                    SubscriptionStatus = table.Column<string>(type: "text", nullable: false),
                    StripeCustomerId = table.Column<string>(type: "text", nullable: true),
                    StripeSubscriptionId = table.Column<string>(type: "text", nullable: true),
                    CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Worlds_TenantId_CreatedAt",
                table: "Worlds",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_TenantId_CreatedAt",
                table: "Workspaces",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantId_CreatedAt",
                table: "Tasks",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEntries_TenantId_AgentId_Key",
                table: "MemoryEntries",
                columns: new[] { "TenantId", "AgentId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId_Timestamp",
                table: "Events",
                columns: new[] { "TenantId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId",
                table: "Agents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TenantId",
                table: "Invitations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_UserId",
                table: "Memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_StripeCustomerId",
                table: "Tenants",
                column: "StripeCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "BillingEvents");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Worlds_TenantId_CreatedAt",
                table: "Worlds");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_TenantId_CreatedAt",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_TenantId_CreatedAt",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_MemoryEntries_TenantId_AgentId_Key",
                table: "MemoryEntries");

            migrationBuilder.DropIndex(
                name: "IX_Events_TenantId_Timestamp",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Agents_TenantId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Worlds");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ToolCalls");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "MemoryEntries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Artifacts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Agents");

            migrationBuilder.CreateIndex(
                name: "IX_Worlds_CreatedAt",
                table: "Worlds",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_CreatedAt",
                table: "Workspaces",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEntries_AgentId_Key",
                table: "MemoryEntries",
                columns: new[] { "AgentId", "Key" });
        }
    }
}
