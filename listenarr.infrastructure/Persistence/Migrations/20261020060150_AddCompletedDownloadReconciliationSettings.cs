using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletedDownloadReconciliationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticImportReconciledDownloads",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletedDownloadMinimumStabilitySeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "CompletedDownloadReconciliationIntervalSeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<bool>(
                name: "EnableCompletedDownloadReconciliation",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomaticImportReconciledDownloads",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "CompletedDownloadMinimumStabilitySeconds",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "CompletedDownloadReconciliationIntervalSeconds",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "EnableCompletedDownloadReconciliation",
                table: "ApplicationSettings");
        }
    }
}
