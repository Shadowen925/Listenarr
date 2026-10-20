using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticSearchQueryDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DistinctCandidateCount",
                table: "AutomaticSearchDiagnostics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IdentityMatchCount",
                table: "AutomaticSearchDiagnostics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IndexerOutcomes",
                table: "AutomaticSearchDiagnostics",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "QualityRejectedCount",
                table: "AutomaticSearchDiagnostics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "QueriesAttempted",
                table: "AutomaticSearchDiagnostics",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DistinctCandidateCount",
                table: "AutomaticSearchDiagnostics");

            migrationBuilder.DropColumn(
                name: "IdentityMatchCount",
                table: "AutomaticSearchDiagnostics");

            migrationBuilder.DropColumn(
                name: "IndexerOutcomes",
                table: "AutomaticSearchDiagnostics");

            migrationBuilder.DropColumn(
                name: "QualityRejectedCount",
                table: "AutomaticSearchDiagnostics");

            migrationBuilder.DropColumn(
                name: "QueriesAttempted",
                table: "AutomaticSearchDiagnostics");
        }
    }
}
