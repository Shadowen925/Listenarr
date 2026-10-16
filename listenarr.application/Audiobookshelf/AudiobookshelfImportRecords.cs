using Listenarr.Application.Audiobookshelf.Models;

namespace Listenarr.Application.Audiobookshelf;

/// <summary>Pure helpers that translate Audiobookshelf items into preview rows and audiobook records.</summary>
internal static class AudiobookshelfImportRecords
{
    internal const string CoverRoute = "/api/v1/audiobookshelf/items/{0}/cover";

    internal static bool IsDuplicate(
        AudiobookshelfLibraryItem item,
        string libraryId,
        IReadOnlyCollection<string> mappedPaths,
        IEnumerable<Audiobook> existingAudiobooks)
    {
        foreach (var existing in existingAudiobooks)
        {
            if (string.Equals(existing.AudiobookshelfItemId, item.Id, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(existing.AudiobookshelfLibraryId)
                    || string.Equals(existing.AudiobookshelfLibraryId, libraryId, StringComparison.Ordinal)))
            {
                return true;
            }

            var existingPaths = (existing.Files ?? [])
                .Select(file => file.Path)
                .Append(existing.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path));
            if (existingPaths.Any(existingPath => mappedPaths.Any(mappedPath =>
                    string.Equals(existingPath, mappedPath, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        return false;
    }

    internal static AudiobookshelfImportPreviewItem CreateUnmatchedPreview(
        AudiobookshelfLibraryItem item,
        bool missingIdentifiers,
        string category,
        string reason,
        string? listenarrPath = null,
        IReadOnlyCollection<string>? audioPaths = null) =>
        new(
            item.Id,
            item.Metadata.Title ?? Path.GetFileName(item.Path),
            string.Join(", ", item.Metadata.Authors),
            item.Path,
            listenarrPath,
            null,
            AudiobookshelfImportMatchStatus.Unmatched,
            missingIdentifiers,
            reason,
            item.Metadata.Series,
            item.Metadata.SeriesNumber,
            audioPaths?.Count ?? item.AudioFilePaths.Count,
            DescribeFormats(audioPaths ?? item.AudioFilePaths),
            item.Metadata.Asin,
            item.Metadata.Isbn,
            category);

    internal static string? TryMapItemPath(
        AudiobookshelfLibraryItem item,
        AudiobookshelfLibrary library,
        string rootPath) =>
        AudiobookshelfPathMapper.TryMapPath(item.Path, library.FolderPaths, rootPath, out var mapped)
            ? mapped
            : null;

    internal static IReadOnlyList<string> DescribeFormats(IEnumerable<string> paths) =>
        paths.Select(path => Path.GetExtension(path).TrimStart('.').ToLowerInvariant())
            .Where(extension => extension.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    internal static Audiobook CreateAudiobook(
        AudiobookshelfLibraryItem item,
        AudiobookshelfImportRequest request,
        AudiobookshelfLibrary library,
        RootFolder root,
        IReadOnlyList<string> filePaths)
    {
        var metadata = item.Metadata;
        var basePath = AudiobookshelfPathMapper.TryMapPath(
            item.Path,
            library.FolderPaths,
            root.Path,
            out var mappedItemPath)
            && filePaths.All(path => IsSameOrInsidePath(path, mappedItemPath))
                ? mappedItemPath
                : root.Path;
        var audiobook = new Audiobook
        {
            Title = metadata.Title ?? Path.GetFileName(item.Path),
            Subtitle = metadata.Subtitle,
            Authors = metadata.Authors.ToList(),
            Narrators = metadata.Narrators.ToList(),
            Series = metadata.Series,
            SeriesNumber = metadata.SeriesNumber,
            Description = metadata.Description,
            ImageUrl = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                CoverRoute,
                Uri.EscapeDataString(item.Id)),
            Asin = metadata.Asin,
            Isbn = metadata.Isbn.ToList(),
            OpenLibraryId = metadata.OpenLibraryId,
            Publisher = metadata.Publisher,
            Language = metadata.Language,
            PublishYear = metadata.PublishedYear,
            Genres = metadata.Genres.ToList(),
            AudiobookshelfLibraryId = request.LibraryId,
            AudiobookshelfItemId = item.Id,
            AudiobookshelfMetadataAuthoritative = true,
            Monitored = request.Monitored,
            BasePath = basePath,
            FilePath = filePaths.Count == 1 ? filePaths[0] : null
        };
        AudiobookSeriesMembershipHelper.ApplyToAudiobook(
            audiobook,
            metadata.SeriesMemberships?.Select((membership, index) => new AudiobookSeriesMembership
            {
                SeriesName = membership.Name,
                SeriesNumber = membership.Position,
                AudiobookshelfSeriesId = membership.SourceId,
                SeriesAsin = membership.Asin,
                IsPrimary = index == 0
            }),
            metadata.Series,
            metadata.SeriesNumber);

        foreach (var (type, value) in metadata.Isbn
            .Select(value => (Type: AudiobookExternalIdentifierType.Isbn, Value: (string?)value))
            .Prepend((Type: AudiobookExternalIdentifierType.Asin, Value: metadata.Asin))
            .Prepend((Type: AudiobookExternalIdentifierType.OpenLibraryId, Value: metadata.OpenLibraryId)))
        {
            if (!AudiobookIdentifierNormalizer.TryNormalize(type, value, out var normalized, out _))
            {
                continue;
            }

            audiobook.ExternalIdentifiers ??= [];
            audiobook.ExternalIdentifiers.Add(new AudiobookExternalIdentifier
            {
                Type = type,
                ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(value),
                ValueNormalized = normalized,
                Source = AudiobookExternalIdentifierSource.Imported,
                IsPrimary = audiobook.ExternalIdentifiers.All(identifier => identifier.Type != type)
            });
        }

        return audiobook;
    }

    internal static bool IsSameOrInsidePath(string candidatePath, string rootPath)
    {
        try
        {
            var fullRoot = Path.GetFullPath(rootPath);
            var fullCandidate = Path.GetFullPath(candidatePath);
            var relative = Path.GetRelativePath(fullRoot, fullCandidate);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
