using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticSearchDiagnosticsAndEditionClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartNumber",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProductionType",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalParts",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomaticSearchDiagnostics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    SearchTimestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SearchTrigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IndexersQueried = table.Column<string>(type: "TEXT", nullable: false),
                    ResultsReturned = table.Column<int>(type: "INTEGER", nullable: false),
                    RejectionReasons = table.Column<string>(type: "TEXT", nullable: false),
                    SubmissionAttempts = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedRelease = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DownloadClient = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    FinalOutcome = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomaticSearchDiagnostics", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomaticSearchDiagnostics_AudiobookId_SearchTimestamp",
                table: "AutomaticSearchDiagnostics",
                columns: new[] { "AudiobookId", "SearchTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomaticSearchDiagnostics_SearchTimestamp",
                table: "AutomaticSearchDiagnostics",
                column: "SearchTimestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomaticSearchDiagnostics");

            migrationBuilder.DropColumn(
                name: "PartNumber",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "ProductionType",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "TotalParts",
                table: "Audiobooks");
        }
    }
}
