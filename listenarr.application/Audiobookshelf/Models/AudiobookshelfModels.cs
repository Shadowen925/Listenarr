namespace Listenarr.Application.Audiobookshelf.Models;

public sealed record AudiobookshelfLibrary(
    string Id,
    string Name,
    string? MediaType,
    IReadOnlyList<string> FolderPaths);

public sealed record AudiobookshelfMetadata(
    string? Title,
    string? Subtitle,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Narrators,
    string? Series,
    string? SeriesNumber,
    string? Description,
    string? CoverPath,
    string? Asin,
    IReadOnlyList<string> Isbn,
    string? OpenLibraryId,
    string? Publisher,
    string? Language,
    string? PublishedYear,
    IReadOnlyList<string> Genres,
    IReadOnlyList<AudiobookshelfSeriesMembership>? SeriesMemberships = null,
    AudiobookshelfSeriesMetadataState SeriesMetadataState = AudiobookshelfSeriesMetadataState.NotProvided,
    int SourceSeriesCount = 0,
    string? SeriesMetadataFailureReason = null);

public enum AudiobookshelfSeriesMetadataState
{
    NotProvided,
    NoSeries,
    Parsed,
    ParseError
}

public sealed record AudiobookshelfSeriesMembership(
    string Name,
    string? Position,
    string? SourceId = null,
    string? Asin = null);

public sealed record AudiobookshelfLibraryItem(
    string Id,
    string Path,
    IReadOnlyList<string> AudioFilePaths,
    AudiobookshelfMetadata Metadata,
    string? SourceUnavailableReason = null);

public enum AudiobookshelfImportMatchStatus
{
    Matched,
    Unmatched,
    Duplicate
}

public sealed record AudiobookshelfImportPreviewItem(
    string ItemId,
    string Title,
    string Author,
    string AudiobookshelfPath,
    string? ListenarrPath,
    int? RootFolderId,
    AudiobookshelfImportMatchStatus Status,
    bool MissingIdentifiers,
    string? Reason,
    string? Series = null,
    string? SeriesPosition = null,
    int FileCount = 0,
    IReadOnlyList<string>? FileFormats = null,
    string? Asin = null,
    IReadOnlyList<string>? Isbn = null,
    string? FailureCategory = null);

public static class AudiobookshelfImportFailureCategories
{
    public const string LibraryNotMapped = "LibraryNotMapped";
    public const string AmbiguousLibraryMapping = "AmbiguousLibraryMapping";
    public const string NoMappableAudioFiles = "NoMappableAudioFiles";
    public const string AudioFilesMissing = "AudioFilesMissing";
    public const string Duplicate = "Duplicate";
}

public enum AudiobookshelfImportItemOutcome
{
    Imported,
    Skipped,
    Failed
}

public sealed record AudiobookshelfImportItemResult(
    string ItemId,
    string Title,
    AudiobookshelfImportItemOutcome Outcome,
    string? Reason);

public sealed record AudiobookshelfImportPreview(
    string LibraryId,
    IReadOnlyList<AudiobookshelfImportPreviewItem> Items);

public sealed record AudiobookshelfImportRequest(
    string LibraryId,
    IReadOnlyList<string> ItemIds,
    bool Monitored = true);

public sealed record AudiobookshelfImportResult(
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<string> Messages,
    int FailedCount = 0,
    string LibraryId = "",
    DateTimeOffset ImportedAt = default,
    int SelectedCount = 0,
    IReadOnlyList<AudiobookshelfImportItemResult>? Items = null);

public enum AudiobookshelfSeriesSyncStatus
{
    NeedsUpdate,
    UpToDate,
    NoSeries,
    SourceUnavailable,
    ParseError,
    Conflict,
    MissingInAudiobookshelf
}

public sealed record AudiobookshelfSeriesSyncPreviewItem(
    string ItemId,
    int AudiobookId,
    string Title,
    AudiobookshelfSeriesSyncStatus Status,
    IReadOnlyList<AudiobookSeriesMembershipPreview> CurrentSeries,
    IReadOnlyList<AudiobookSeriesMembershipPreview> AudiobookshelfSeries,
    int SourceSeriesCount = 0,
    int ParsedSeriesCount = 0,
    int ListenarrSeriesCount = 0,
    IReadOnlyList<AudiobookSeriesMembershipPreview>? ProposedSeries = null,
    string? FailureReason = null,
    string? Author = null);

public sealed record AudiobookSeriesMembershipPreview(
    string Name,
    string? Position,
    string? SeriesId = null,
    string? SeriesAsin = null,
    string? AudiobookshelfSeriesId = null);

public sealed record AudiobookshelfSeriesSyncSummary(
    int NeedsUpdate,
    int UpToDate,
    int NoSeries,
    int SourceUnavailable,
    int ParseError,
    int Conflict,
    int MissingInAudiobookshelf);

public sealed record AudiobookshelfSeriesSyncPreview(
    string LibraryId,
    IReadOnlyList<AudiobookshelfSeriesSyncPreviewItem> Items,
    AudiobookshelfSeriesSyncSummary? Summary = null);

public sealed record AudiobookshelfSeriesSyncRequest(string LibraryId, IReadOnlyList<string> ItemIds);

public enum AudiobookshelfSeriesSyncOutcome
{
    Updated,
    Skipped,
    Failed
}

public sealed record AudiobookshelfSeriesSyncItemResult(
    string ItemId,
    string Title,
    AudiobookshelfSeriesSyncOutcome Outcome,
    string? Reason);

public sealed record AudiobookshelfSeriesSyncResult(
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<AudiobookshelfSeriesSyncItemResult> Items);

public sealed record AudiobookshelfImage(byte[] Content, string ContentType);
