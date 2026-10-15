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
    IReadOnlyList<string> Genres);

public sealed record AudiobookshelfLibraryItem(
    string Id,
    string Path,
    IReadOnlyList<string> AudioFilePaths,
    AudiobookshelfMetadata Metadata);

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
    IReadOnlyList<string> Messages);

public sealed record AudiobookshelfImage(byte[] Content, string ContentType);
