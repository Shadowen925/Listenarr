using Listenarr.Application.Audiobookshelf.Contracts;
using Listenarr.Application.Audiobookshelf.Models;
using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobookshelf;

public sealed class AudiobookshelfIntegrationService(
    IAudiobookshelfClient client,
    IConfigurationService configurationService,
    IRootFolderRepository rootFolderRepository,
    IAudiobookRepository audiobookRepository,
    IAudiobookDeletionCommitService audiobookDeletionCommitService,
    IAudiobookScanService audiobookScanService,
    IFileSystem fileSystem,
    ILogger<AudiobookshelfIntegrationService> logger) : IAudiobookshelfIntegrationService
{
    private const string CoverRoute = "/api/v1/audiobookshelf/items/{0}/cover";

    public Task<AudiobookshelfImportConnectionSettings> GetConnectionSettingsAsync() =>
        configurationService.GetAudiobookshelfImportSettingsAsync();

    public Task<AudiobookshelfImportConnectionSettings> SaveConnectionSettingsAsync(
        AudiobookshelfImportConnectionSettings settings) =>
        configurationService.SaveAudiobookshelfImportSettingsAsync(settings);

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredConnectionAsync();
        await client.TestConnectionAsync(connection.Url, connection.ApiKey!, cancellationToken);
    }

    public async Task<IReadOnlyList<AudiobookshelfLibrary>> GetLibrariesAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredConnectionAsync();
        return await client.GetLibrariesAsync(
            connection.Url,
            connection.ApiKey!,
            cancellationToken);
    }

    public async Task<AudiobookshelfImportPreview> PreviewImportAsync(
        string libraryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        var connection = await GetRequiredConnectionAsync();
        var libraries = await client.GetLibrariesAsync(
            connection.Url,
            connection.ApiKey!,
            cancellationToken);
        var library = libraries.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, libraryId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("Audiobookshelf library was not found.");
        var libraryItems = await client.GetLibraryItemsAsync(
            connection.Url,
            connection.ApiKey!,
            libraryId,
            cancellationToken);
        var roots = await rootFolderRepository.GetAllAsync();
        var mappedRoots = roots.Where(root =>
            string.Equals(
                root.AudiobookshelfLibraryId,
                libraryId,
                StringComparison.Ordinal)).ToList();
        var existingAudiobooks = await audiobookRepository.GetLibraryAsync();
        var previews = new List<AudiobookshelfImportPreviewItem>(libraryItems.Count);
        var seenItemIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in libraryItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var missingIdentifiers = string.IsNullOrWhiteSpace(item.Metadata.Asin)
                && item.Metadata.Isbn.Count == 0;
            if (mappedRoots.Count != 1)
            {
                previews.Add(CreateUnmatchedPreview(
                    item,
                    missingIdentifiers,
                    mappedRoots.Count == 0
                        ? "Map this Audiobookshelf library to one Listenarr root folder."
                        : "This Audiobookshelf library is mapped to more than one Listenarr root folder."));
                continue;
            }

            var root = mappedRoots[0];
            var audioPaths = ResolveAudioPaths(item, library, root.Path);
            if (audioPaths.Count == 0 || audioPaths.Any(path => !fileSystem.FileExists(path)))
            {
                previews.Add(CreateUnmatchedPreview(
                    item,
                    missingIdentifiers,
                    "The audiobook files could not be mapped to existing Listenarr files."));
                continue;
            }

            var mappedItemPath = AudiobookshelfPathMapper.TryMapPath(
                item.Path,
                library.FolderPaths,
                root.Path,
                out var listenarrPath)
                    ? listenarrPath
                    : fileSystem.GetParentDirectory(audioPaths[0]) ?? root.Path;

            var duplicate = IsDuplicate(item, libraryId, audioPaths, existingAudiobooks)
                || !seenItemIds.Add(item.Id)
                || audioPaths.Any(seenPaths.Contains);
            foreach (var audioPath in audioPaths)
            {
                seenPaths.Add(audioPath);
            }

            previews.Add(new AudiobookshelfImportPreviewItem(
                item.Id,
                item.Metadata.Title ?? Path.GetFileName(item.Path),
                string.Join(", ", item.Metadata.Authors),
                item.Path,
                mappedItemPath,
                root.Id,
                duplicate
                    ? AudiobookshelfImportMatchStatus.Duplicate
                    : AudiobookshelfImportMatchStatus.Matched,
                missingIdentifiers,
                duplicate ? "An existing Listenarr audiobook already matches this record or file path." : null));
        }

        return new AudiobookshelfImportPreview(libraryId, previews);
    }

    public async Task<AudiobookshelfImportResult> ImportAsync(
        AudiobookshelfImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LibraryId);
        ArgumentNullException.ThrowIfNull(request.ItemIds);

        var preview = await PreviewImportAsync(request.LibraryId, cancellationToken);
        var selectedIds = request.ItemIds
            .Where(itemId => !string.IsNullOrWhiteSpace(itemId))
            .ToHashSet(StringComparer.Ordinal);
        var selected = preview.Items
            .Where(item => selectedIds.Contains(item.ItemId))
            .ToList();
        if (selected.Count != selectedIds.Count)
        {
            throw new ArgumentException(
                "One or more selected Audiobookshelf items do not belong to this library.",
                nameof(request));
        }

        var connection = await GetRequiredConnectionAsync();
        var libraries = await client.GetLibrariesAsync(
            connection.Url,
            connection.ApiKey!,
            cancellationToken);
        var library = libraries.Single(candidate =>
            string.Equals(candidate.Id, request.LibraryId, StringComparison.Ordinal));
        var sourceItems = await client.GetLibraryItemsAsync(
            connection.Url,
            connection.ApiKey!,
            request.LibraryId,
            cancellationToken);
        var sourceById = sourceItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var roots = await rootFolderRepository.GetAllAsync();
        var rootById = roots.ToDictionary(root => root.Id);
        var existingAudiobooks = await audiobookRepository.GetLibraryAsync();
        var importedItemIds = new HashSet<string>(StringComparer.Ordinal);
        var importedPaths = new HashSet<string>(StringComparer.Ordinal);
        var imported = 0;
        var skipped = 0;
        var messages = new List<string>();

        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Status != AudiobookshelfImportMatchStatus.Matched
                || item.RootFolderId is not int rootFolderId
                || !rootById.TryGetValue(rootFolderId, out var root)
                || !sourceById.TryGetValue(item.ItemId, out var source))
            {
                skipped++;
                messages.Add($"{item.Title}: skipped because no safe in-place path match was available.");
                continue;
            }

            var filePaths = ResolveAudioPaths(source, library, root.Path);
            if (filePaths.Count == 0 || filePaths.Any(path => !fileSystem.FileExists(path))
                || IsDuplicate(source, request.LibraryId, filePaths, existingAudiobooks)
                || importedItemIds.Contains(source.Id)
                || filePaths.Any(importedPaths.Contains))
            {
                skipped++;
                messages.Add($"{item.Title}: skipped because the record is now missing or already imported.");
                continue;
            }

            var audiobook = CreateAudiobook(source, request, library, root, filePaths);
            try
            {
                await audiobookRepository.AddAsync(audiobook);
                foreach (var filePath in filePaths)
                {
                    if (!await audiobookScanService.RegisterExistingFileAsync(
                            audiobook.Id,
                            audiobook.BasePath!,
                            filePath,
                            "audiobookshelf-import",
                            cancellationToken))
                    {
                        throw new InvalidOperationException(
                            $"Listenarr could not safely register an existing Audiobookshelf file for '{audiobook.Title}'.");
                    }
                }

                existingAudiobooks.Add(audiobook);
                importedItemIds.Add(source.Id);
                foreach (var filePath in filePaths)
                {
                    importedPaths.Add(filePath);
                }
                imported++;
            }
            catch (UniqueConstraintViolationException)
            {
                skipped++;
                messages.Add($"{item.Title}: skipped because it was imported concurrently.");
            }
            catch (Exception exception) when (
                exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                if (audiobook.Id > 0)
                {
                    var deletion = await audiobookDeletionCommitService.DeleteAsync(audiobook.Id);
                    if (deletion.Outcome == AudiobookDeletionCommitOutcome.Failed)
                    {
                        logger.LogError(
                            "Failed to roll back Audiobookshelf import record {AudiobookId}",
                            audiobook.Id);
                    }
                }

                logger.LogError(
                    exception,
                    "Non-destructive Audiobookshelf import failed for item {ItemId}",
                    item.ItemId);
                messages.Add($"{item.Title}: import failed; existing files were left unchanged.");
            }
        }

        return new AudiobookshelfImportResult(imported, skipped, messages);
    }

    public async Task<bool> TriggerLibraryScanAsync(
        string libraryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        var connection = await GetRequiredConnectionAsync();
        await client.TriggerLibraryScanAsync(
            connection.Url,
            connection.ApiKey!,
            libraryId,
            cancellationToken);
        return true;
    }

    public async Task<bool> TriggerScanForAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        var audiobook = await audiobookRepository.GetByIdSnapshotAsync(audiobookId, cancellationToken);
        if (audiobook == null || string.IsNullOrWhiteSpace(audiobook.BasePath))
        {
            return false;
        }

        var roots = await rootFolderRepository.GetAllAsync();
        var matchingRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root.AudiobookshelfLibraryId)
                && IsSameOrInsidePath(audiobook.BasePath, root.Path))
            .ToList();
        if (matchingRoots.Count == 0)
        {
            return false;
        }

        if (matchingRoots.Count > 1)
        {
            throw new InvalidOperationException(
                $"Audiobook {audiobookId} is under more than one mapped Audiobookshelf root folder.");
        }

        await TriggerLibraryScanAsync(
            matchingRoots[0].AudiobookshelfLibraryId!,
            cancellationToken);
        return true;
    }

    public async Task<AudiobookshelfImage> GetItemCoverAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var connection = await GetRequiredConnectionAsync();
        return await client.GetItemCoverAsync(
            connection.Url,
            connection.ApiKey!,
            itemId,
            cancellationToken);
    }

    private async Task<AudiobookshelfImportConnectionSettings> GetRequiredConnectionAsync()
    {
        var settings = await configurationService.GetAudiobookshelfImportSettingsAsync(includeSecret: true);
        if (string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException(
                "Audiobookshelf server URL and API key must be configured before using this integration.");
        }

        return settings;
    }

    private List<string> ResolveAudioPaths(
        AudiobookshelfLibraryItem item,
        AudiobookshelfLibrary library,
        string rootPath)
    {
        var result = new List<string>();
        foreach (var sourcePath in item.AudioFilePaths)
        {
            var candidate = sourcePath.StartsWith("/", StringComparison.Ordinal)
                || sourcePath.StartsWith("\\", StringComparison.Ordinal)
                    ? sourcePath
                    : $"{item.Path.TrimEnd('/', '\\')}/{sourcePath.TrimStart('/', '\\')}";
            if (AudiobookshelfPathMapper.TryMapPath(
                    candidate,
                    library.FolderPaths,
                    rootPath,
                    out var mapped)
                && FileUtils.IsAudioFile(mapped))
            {
                result.Add(mapped);
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsDuplicate(
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

    private static AudiobookshelfImportPreviewItem CreateUnmatchedPreview(
        AudiobookshelfLibraryItem item,
        bool missingIdentifiers,
        string reason) =>
        new(
            item.Id,
            item.Metadata.Title ?? Path.GetFileName(item.Path),
            string.Join(", ", item.Metadata.Authors),
            item.Path,
            null,
            null,
            AudiobookshelfImportMatchStatus.Unmatched,
            missingIdentifiers,
            reason);

    private static Audiobook CreateAudiobook(
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

    private static bool IsSameOrInsidePath(string candidatePath, string rootPath)
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
