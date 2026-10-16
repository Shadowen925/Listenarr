using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStalledDownloadSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StalledAutoBlocklist",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StalledAutoSearchAlternatives",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StalledCleanupBehavior",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StalledHandlingEnabled",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StalledIgnoreManuallyPaused",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StalledMaxReplacementAttempts",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StalledMetadataTimeoutMinutes",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StalledMinimumProgressBytes",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StalledRetryDelayMinutes",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StalledTimeoutMinutes",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StalledAutoBlocklist",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledAutoSearchAlternatives",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledCleanupBehavior",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledHandlingEnabled",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledIgnoreManuallyPaused",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledMaxReplacementAttempts",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledMetadataTimeoutMinutes",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledMinimumProgressBytes",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledRetryDelayMinutes",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "StalledTimeoutMinutes",
                table: "ApplicationSettings");
        }
    }
}
