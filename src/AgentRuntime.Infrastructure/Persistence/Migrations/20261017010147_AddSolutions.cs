using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolutionConversations",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    InstallId = table.Column<string>(type: "text", nullable: false),
                    EndUserId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionConversations", x => x.TaskId);
                });

            migrationBuilder.CreateTable(
                name: "SolutionInstalls",
                columns: table => new
                {
                    InstallId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    BundleJson = table.Column<string>(type: "text", nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: false),
                    WorkspacesJson = table.Column<string>(type: "text", nullable: false),
                    HistoryJson = table.Column<string>(type: "text", nullable: false),
                    InstalledBy = table.Column<string>(type: "text", nullable: true),
                    InstalledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionInstalls", x => x.InstallId);
                });

            migrationBuilder.CreateTable(
                name: "SolutionPackageNames",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionPackageNames", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "SolutionPackages",
                columns: table => new
                {
                    PackageId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    PublisherTenantId = table.Column<string>(type: "text", nullable: false),
                    PublisherName = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Visibility = table.Column<string>(type: "text", nullable: false),
                    BundleJson = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    Signature = table.Column<string>(type: "text", nullable: false),
                    Yanked = table.Column<bool>(type: "boolean", nullable: false),
                    YankReason = table.Column<string>(type: "text", nullable: true),
                    Installs = table.Column<int>(type: "integer", nullable: false),
                    PublishedBy = table.Column<string>(type: "text", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionPackages", x => x.PackageId);
                });

            migrationBuilder.CreateTable(
                name: "SolutionRecordChanges",
                columns: table => new
                {
                    ChangeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    InstallId = table.Column<string>(type: "text", nullable: false),
                    Entity = table.Column<string>(type: "text", nullable: false),
                    RecordId = table.Column<string>(type: "text", nullable: false),
                    Event = table.Column<string>(type: "text", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    PreviousJson = table.Column<string>(type: "text", nullable: true),
                    ChangedBy = table.Column<string>(type: "text", nullable: false),
                    TaskId = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WorkspacesNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionRecordChanges", x => x.ChangeId);
                });

            migrationBuilder.CreateTable(
                name: "SolutionRecords",
                columns: table => new
                {
                    RecordId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    InstallId = table.Column<string>(type: "text", nullable: false),
                    Entity = table.Column<string>(type: "text", nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    EndUserId = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionRecords", x => x.RecordId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionConversations_TenantId_InstallId_EndUserId_CreatedAt",
                table: "SolutionConversations",
                columns: new[] { "TenantId", "InstallId", "EndUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionInstalls_TenantId_Name",
                table: "SolutionInstalls",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionPackages_Name_PublishedAt",
                table: "SolutionPackages",
                columns: new[] { "Name", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionPackages_Visibility_PublisherTenantId",
                table: "SolutionPackages",
                columns: new[] { "Visibility", "PublisherTenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionRecordChanges_DispatchedAt",
                table: "SolutionRecordChanges",
                column: "DispatchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SolutionRecordChanges_TenantId_InstallId_TaskId",
                table: "SolutionRecordChanges",
                columns: new[] { "TenantId", "InstallId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionRecords_DataJson",
                table: "SolutionRecords",
                column: "DataJson")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_SolutionRecords_TenantId_InstallId_Entity_EndUserId",
                table: "SolutionRecords",
                columns: new[] { "TenantId", "InstallId", "Entity", "EndUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionRecords_TenantId_InstallId_Entity_UpdatedAt",
                table: "SolutionRecords",
                columns: new[] { "TenantId", "InstallId", "Entity", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolutionConversations");

            migrationBuilder.DropTable(
                name: "SolutionInstalls");

            migrationBuilder.DropTable(
                name: "SolutionPackageNames");

            migrationBuilder.DropTable(
                name: "SolutionPackages");

            migrationBuilder.DropTable(
                name: "SolutionRecordChanges");

            migrationBuilder.DropTable(
                name: "SolutionRecords");
        }
    }
}
