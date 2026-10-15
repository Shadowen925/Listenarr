using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

internal static class ListenarrDatabaseMigrationPreflight
{
    internal const string DurableFilesystemRecoveryMigrationId =
        "20260810160602_AddDurableFilesystemRecovery";
    internal const string RootFoldersMigrationId =
        "20260101172733_AddRootFolders";
    internal const string AudiobookshelfIntegrationMigrationId =
        "20261015190028_AddAudiobookshelfIntegration";
    private const string LegacyFolderNamingPattern = "{Author}/{Series}/{Title}";
    private const string LegacyFileNamingPattern = "{Title}";
    private const string LegacyMultiFileNamingPatternWithoutChapter =
        "{Title}-{DiskNumber:00}";
    private const string LegacyMultiFileNamingPattern = "{Title}-{DiskNumber:00}-{ChapterNumber:00}";
    private const string CurrentFolderNamingPattern =
        "{Author} - {Series}/{Author} - {Series} - {SeriesBook} - {Title}";
    private const string CurrentFileNamingPattern =
        "{Author} - {Series} - {SeriesBook} - {Title}";
    private const string CurrentMultiFileNamingPattern =
        "{Author} - {Series} - {SeriesBook} - {Title} - Part {DiskNumber:00}-{ChapterNumber:00}";

    public static ListenarrDatabaseMigrationPreflightResult RepairLegacyData(
        ListenArrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = context.Database.GetAppliedMigrations()
            .ToHashSet(StringComparer.Ordinal);
        var normalizeDefaultRoots =
            applied.Contains(RootFoldersMigrationId)
            && !applied.Contains(DurableFilesystemRecoveryMigrationId);
        if (!normalizeDefaultRoots)
        {
            return default;
        }

        using var transaction = context.Database.BeginTransaction();
        var defaultRootsNormalized = context.Database.ExecuteSqlRaw(
            """
            UPDATE "RootFolders"
            SET "IsDefault" = 0
            WHERE "IsDefault" = 1
              AND "Id" <> (
                  SELECT MIN("Id")
                  FROM "RootFolders"
                  WHERE "IsDefault" = 1
              );
            """);
        transaction.Commit();

        return new ListenarrDatabaseMigrationPreflightResult(defaultRootsNormalized);
    }

    public static ListenarrDatabasePostMigrationRepairResult RepairPostMigrationData(
        ListenArrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = context.Database.GetAppliedMigrations()
            .ToHashSet(StringComparer.Ordinal);
        var repairLegacyMoveJobs =
            applied.Contains(DurableFilesystemRecoveryMigrationId);
        var repairDefaultNamingPatterns =
            applied.Contains(AudiobookshelfIntegrationMigrationId);
        if (!repairLegacyMoveJobs && !repairDefaultNamingPatterns)
        {
            return default;
        }

        using var transaction = context.Database.BeginTransaction();
        var moveJobsRepaired = repairLegacyMoveJobs
            ? context.Database.ExecuteSqlRaw(
                """
            UPDATE "MoveJobs"
            SET
                "Status" = 'NeedsAttention',
                "Error" = 'This move job was created by a pre-durable released version and cannot be resumed safely after upgrade.',
                "FailureKind" = 'Verification',
                "ActiveDeduplicationKey" = NULL,
                "UpdatedAt" = CURRENT_TIMESTAMP
            WHERE "ExecutionProtocolVersion" = 0
              AND "Status" NOT IN ('Completed', 'Failed')
              AND (
                  "Status" <> 'NeedsAttention'
                  OR "ActiveDeduplicationKey" IS NOT NULL
                  OR "FailureKind" <> 'Verification'
                  OR "Error" IS NULL
              );
            """)
            : 0;
        var namingDefaultsUpdated = repairDefaultNamingPatterns
            ? UpdateDefaultNamingPatterns(context)
            : 0;
        transaction.Commit();

        return new ListenarrDatabasePostMigrationRepairResult(
            moveJobsRepaired,
            namingDefaultsUpdated);
    }

    private static int UpdateDefaultNamingPatterns(ListenArrDbContext context)
    {
        var updated = context.Database.ExecuteSqlRaw(
            """
            UPDATE "ApplicationSettings"
            SET "FolderNamingPattern" = {0}
            WHERE "FolderNamingPattern" = {1};
            """,
            CurrentFolderNamingPattern,
            LegacyFolderNamingPattern);
        updated += context.Database.ExecuteSqlRaw(
            """
            UPDATE "ApplicationSettings"
            SET "FileNamingPattern" = {0}
            WHERE "FileNamingPattern" = {1};
            """,
            CurrentFileNamingPattern,
            LegacyFileNamingPattern);
        updated += context.Database.ExecuteSqlRaw(
            """
            UPDATE "ApplicationSettings"
            SET "MultiFileNamingPattern" = {0}
            WHERE "MultiFileNamingPattern" IN ({1}, {2});
            """,
            CurrentMultiFileNamingPattern,
            LegacyMultiFileNamingPatternWithoutChapter,
            LegacyMultiFileNamingPattern);
        return updated;
    }
}

internal readonly record struct ListenarrDatabaseMigrationPreflightResult(
    int DefaultRootsNormalized);

internal readonly record struct ListenarrDatabasePostMigrationRepairResult(
    int MoveJobsRepaired,
    int NamingDefaultsUpdated);
