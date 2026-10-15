using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudiobookshelfIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfLibraryId",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfItemId",
                table: "Audiobooks",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfLibraryId",
                table: "Audiobooks",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AudiobookshelfMetadataAuthoritative",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfApiKeyEncrypted",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfUrl",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootFolders_AudiobookshelfLibraryId",
                table: "RootFolders",
                column: "AudiobookshelfLibraryId",
                unique: true,
                filter: "\"AudiobookshelfLibraryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Audiobooks_AudiobookshelfItem",
                table: "Audiobooks",
                columns: new[] { "AudiobookshelfLibraryId", "AudiobookshelfItemId" },
                unique: true,
                filter: "\"AudiobookshelfItemId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RootFolders_AudiobookshelfLibraryId",
                table: "RootFolders");

            migrationBuilder.DropIndex(
                name: "IX_Audiobooks_AudiobookshelfItem",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfLibraryId",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfItemId",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfLibraryId",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfMetadataAuthoritative",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfApiKeyEncrypted",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfUrl",
                table: "ApplicationSettings");
        }
    }
}
