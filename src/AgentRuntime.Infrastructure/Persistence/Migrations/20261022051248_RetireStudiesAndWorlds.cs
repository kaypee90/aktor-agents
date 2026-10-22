using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRuntime.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Studies and the world simulation moved to the Studies product (branch product/studies), so
    /// their entities left this model. Their tables (Studies, StudyDatasets, StudyEvidence,
    /// StudyHypotheses, StudyModels, StudyReports, StudySimulations, StudySourceRoles, Worlds) are
    /// deliberately left in place: nothing is dropped, so a database shared with that product keeps
    /// its data. Drop them by hand if you don't need them.
    /// </summary>
    public partial class RetireStudiesAndWorlds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
