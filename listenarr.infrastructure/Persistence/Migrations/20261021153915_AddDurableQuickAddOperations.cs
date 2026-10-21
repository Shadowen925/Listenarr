using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableQuickAddOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuickAddOperationId",
                table: "Downloads",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuickAddOperationId",
                table: "DownloadProcessingJobs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuickAddOperationId",
                table: "AutomaticSearchDiagnostics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuickAddOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RequestKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    AudiobookTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    DiagnosticId = table.Column<int>(type: "INTEGER", nullable: true),
                    SelectedRelease = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DownloadClientId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DownloadId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProcessingJobId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuickAddOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuickAddOperations_AudiobookId",
                table: "QuickAddOperations",
                column: "AudiobookId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuickAddOperations_RequestKey",
                table: "QuickAddOperations",
                column: "RequestKey",
                unique: true,
                filter: "\"State\" <> 12");

            migrationBuilder.CreateIndex(
                name: "IX_QuickAddOperations_State_UpdatedAt",
                table: "QuickAddOperations",
                columns: new[] { "State", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuickAddOperations");

            migrationBuilder.DropColumn(
                name: "QuickAddOperationId",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "QuickAddOperationId",
                table: "DownloadProcessingJobs");

            migrationBuilder.DropColumn(
                name: "QuickAddOperationId",
                table: "AutomaticSearchDiagnostics");
        }
    }
}
