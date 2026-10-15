using Listenarr.Application.Audiobookshelf.Contracts;
using Listenarr.Application.Audiobookshelf.Models;
using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;
using static Listenarr.Application.Audiobookshelf.AudiobookshelfImportRecords;

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
                        ? AudiobookshelfImportFailureCategories.LibraryNotMapped
                        : AudiobookshelfImportFailureCategories.AmbiguousLibraryMapping,
                    mappedRoots.Count == 0
                        ? "Map this Audiobookshelf library to one Listenarr root folder."
                        : "This Audiobookshelf library is mapped to more than one Listenarr root folder."));
                continue;
            }

            var root = mappedRoots[0];
            var audioPaths = ResolveAudioPaths(item, library, root.Path);
            if (audioPaths.Count == 0 || audioPaths.Any(path => !fileSystem.FileExists(path)))
            {
                var missing = audioPaths.Where(path => !fileSystem.FileExists(path)).ToList();
                previews.Add(CreateUnmatchedPreview(
                    item,
                    missingIdentifiers,
                    audioPaths.Count == 0
                        ? AudiobookshelfImportFailureCategories.NoMappableAudioFiles
                        : AudiobookshelfImportFailureCategories.AudioFilesMissing,
                    audioPaths.Count == 0
                        ? $"None of the {item.AudioFilePaths.Count} Audiobookshelf audio file path(s) could be translated into root folder '{root.Path}'. Check the library folder mapping and container path mappings."
                        : $"{missing.Count} of {audioPaths.Count} translated audio file(s) do not exist in Listenarr (first missing: {missing[0]}).",
                    TryMapItemPath(item, library, root.Path),
                    audioPaths));
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
                duplicate ? "An existing Listenarr audiobook already matches this record or file path." : null,
                item.Metadata.Series,
                item.Metadata.SeriesNumber,
                audioPaths.Count,
                DescribeFormats(audioPaths),
                item.Metadata.Asin,
                item.Metadata.Isbn,
                duplicate ? AudiobookshelfImportFailureCategories.Duplicate : null));
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
        var failed = 0;
        var messages = new List<string>();
        var results = new List<AudiobookshelfImportItemResult>(selected.Count);

        void Record(AudiobookshelfImportPreviewItem entry, AudiobookshelfImportItemOutcome outcome, string? reason)
        {
            results.Add(new AudiobookshelfImportItemResult(entry.ItemId, entry.Title, outcome, reason));
            if (reason != null)
            {
                messages.Add($"{entry.Title}: {reason}");
            }
        }

        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Status != AudiobookshelfImportMatchStatus.Matched
                || item.RootFolderId is not int rootFolderId
                || !rootById.TryGetValue(rootFolderId, out var root)
                || !sourceById.TryGetValue(item.ItemId, out var source))
            {
                skipped++;
                Record(
                    item,
                    AudiobookshelfImportItemOutcome.Skipped,
                    item.Status == AudiobookshelfImportMatchStatus.Duplicate
                        ? "skipped because it is already imported."
                        : "skipped because no safe in-place path match was available.");
                continue;
            }

            var filePaths = ResolveAudioPaths(source, library, root.Path);
            if (filePaths.Count == 0 || filePaths.Any(path => !fileSystem.FileExists(path))
                || IsDuplicate(source, request.LibraryId, filePaths, existingAudiobooks)
                || importedItemIds.Contains(source.Id)
                || filePaths.Any(importedPaths.Contains))
            {
                skipped++;
                Record(
                    item,
                    AudiobookshelfImportItemOutcome.Skipped,
                    "skipped because the record is now missing or already imported.");
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
                results.Add(new AudiobookshelfImportItemResult(
                    item.ItemId,
                    item.Title,
                    AudiobookshelfImportItemOutcome.Imported,
                    null));
            }
            catch (UniqueConstraintViolationException)
            {
                skipped++;
                Record(
                    item,
                    AudiobookshelfImportItemOutcome.Skipped,
                    "skipped because it was imported concurrently.");
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
                failed++;
                Record(
                    item,
                    AudiobookshelfImportItemOutcome.Failed,
                    "import failed; existing files were left unchanged.");
            }
        }

        logger.LogInformation(
            "Audiobookshelf import for library {LibraryId}: selected {Selected}, imported {Imported}, skipped {Skipped}, failed {Failed}",
            request.LibraryId,
            selected.Count,
            imported,
            skipped,
            failed);
        return new AudiobookshelfImportResult(
            imported,
            skipped,
            messages,
            failed,
            request.LibraryId,
            DateTimeOffset.UtcNow,
            selected.Count,
            results);
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
}
