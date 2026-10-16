using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadAuditAndRejections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DownloadEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DownloadId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseRejections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ReleaseTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseRejections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadEvents_DownloadId",
                table: "DownloadEvents",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadEvents_Timestamp",
                table: "DownloadEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseRejections_AudiobookId_ReleaseKey",
                table: "ReleaseRejections",
                columns: new[] { "AudiobookId", "ReleaseKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadEvents");

            migrationBuilder.DropTable(
                name: "ReleaseRejections");
        }
    }
}
