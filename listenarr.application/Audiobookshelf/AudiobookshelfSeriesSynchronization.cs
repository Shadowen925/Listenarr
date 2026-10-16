using Listenarr.Application.Audiobookshelf.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobookshelf;

public sealed partial class AudiobookshelfIntegrationService
{
    public async Task<AudiobookshelfSeriesSyncPreview> PreviewSeriesSyncAsync(
        string libraryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        var (library, sourceItems) = await GetAudiobookshelfLibraryItemsAsync(
            libraryId,
            cancellationToken);
        var sourceById = sourceItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var importedBooks = await audiobookRepository.GetAudiobookshelfBooksAsync(
            libraryId,
            cancellationToken);
        var previews = importedBooks.Select(audiobook =>
        {
            var currentSeries = GetCurrentSeries(audiobook);
            if (audiobook.AudiobookshelfItemId is not string itemId
                || !sourceById.TryGetValue(itemId, out var source))
            {
                return new AudiobookshelfSeriesSyncPreviewItem(
                    audiobook.AudiobookshelfItemId ?? string.Empty,
                    audiobook.Id,
                    audiobook.Title ?? string.Empty,
                    AudiobookshelfSeriesSyncStatus.MissingInAudiobookshelf,
                    currentSeries,
                    []);
            }

            var sourceSeries = GetSourceSeries(source.Metadata);
            return new AudiobookshelfSeriesSyncPreviewItem(
                itemId,
                audiobook.Id,
                audiobook.Title ?? string.Empty,
                AreSeriesEquivalent(currentSeries, sourceSeries)
                    ? AudiobookshelfSeriesSyncStatus.UpToDate
                    : AudiobookshelfSeriesSyncStatus.NeedsUpdate,
                currentSeries,
                sourceSeries);
        }).ToList();

        return new AudiobookshelfSeriesSyncPreview(library.Id, previews);
    }

    public async Task<AudiobookshelfSeriesSyncResult> SynchronizeSeriesAsync(
        AudiobookshelfSeriesSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LibraryId);
        ArgumentNullException.ThrowIfNull(request.ItemIds);

        var selectedIds = request.ItemIds
            .Where(itemId => !string.IsNullOrWhiteSpace(itemId))
            .ToHashSet(StringComparer.Ordinal);
        var preview = await PreviewSeriesSyncAsync(request.LibraryId, cancellationToken);
        if (preview.Items.Count(item => selectedIds.Contains(item.ItemId)) != selectedIds.Count)
        {
            throw new ArgumentException(
                "One or more selected Audiobookshelf items are not imported from this library.",
                nameof(request));
        }

        var (_, sourceItems) = await GetAudiobookshelfLibraryItemsAsync(
            request.LibraryId,
            cancellationToken);
        var sourceById = sourceItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var books = await audiobookRepository.GetAudiobookshelfBooksAsync(
            request.LibraryId,
            cancellationToken);
        var booksByItemId = books
            .Where(audiobook => !string.IsNullOrWhiteSpace(audiobook.AudiobookshelfItemId))
            .ToDictionary(audiobook => audiobook.AudiobookshelfItemId!, StringComparer.Ordinal);
        var results = new List<AudiobookshelfSeriesSyncItemResult>(selectedIds.Count);
        var updated = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var previewItem in preview.Items.Where(item => selectedIds.Contains(item.ItemId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previewItem.Status != AudiobookshelfSeriesSyncStatus.NeedsUpdate
                || !booksByItemId.TryGetValue(previewItem.ItemId, out var audiobook)
                || !sourceById.TryGetValue(previewItem.ItemId, out var source))
            {
                skipped++;
                results.Add(new AudiobookshelfSeriesSyncItemResult(
                    previewItem.ItemId,
                    previewItem.Title,
                    AudiobookshelfSeriesSyncOutcome.Skipped,
                    previewItem.Status == AudiobookshelfSeriesSyncStatus.MissingInAudiobookshelf
                        ? "The source item is no longer present in Audiobookshelf."
                        : "Series metadata is already up to date."));
                continue;
            }

            var sourceSeries = GetSourceSeries(source.Metadata);
            if (AreSeriesEquivalent(GetCurrentSeries(audiobook), sourceSeries))
            {
                skipped++;
                results.Add(new AudiobookshelfSeriesSyncItemResult(
                    previewItem.ItemId,
                    previewItem.Title,
                    AudiobookshelfSeriesSyncOutcome.Skipped,
                    "Series metadata changed after the preview was created."));
                continue;
            }

            var memberships = sourceSeries.Select((membership, index) =>
            {
                var existing = audiobook.SeriesMemberships?.FirstOrDefault(current =>
                    string.Equals(
                        current.SeriesName?.Trim(),
                        membership.Name,
                        StringComparison.OrdinalIgnoreCase));
                return new AudiobookSeriesMembership
                {
                    SeriesName = membership.Name,
                    SeriesNumber = membership.Position,
                    SeriesAsin = existing?.SeriesAsin,
                    IsPrimary = index == 0,
                    SortOrder = index
                };
            });
            AudiobookSeriesMembershipHelper.ApplyToAudiobook(audiobook, memberships);

            try
            {
                if (!await audiobookRepository.UpdateAsync(audiobook))
                {
                    skipped++;
                    results.Add(new AudiobookshelfSeriesSyncItemResult(
                        previewItem.ItemId,
                        previewItem.Title,
                        AudiobookshelfSeriesSyncOutcome.Skipped,
                        "The imported audiobook no longer exists."));
                    continue;
                }

                updated++;
                results.Add(new AudiobookshelfSeriesSyncItemResult(
                    previewItem.ItemId,
                    previewItem.Title,
                    AudiobookshelfSeriesSyncOutcome.Updated,
                    null));
            }
            catch (Exception exception) when (
                exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(
                    exception,
                    "Audiobookshelf series synchronization failed for item {ItemId}",
                    previewItem.ItemId);
                failed++;
                results.Add(new AudiobookshelfSeriesSyncItemResult(
                    previewItem.ItemId,
                    previewItem.Title,
                    AudiobookshelfSeriesSyncOutcome.Failed,
                    "Series metadata could not be updated."));
            }
        }

        logger.LogInformation(
            "Audiobookshelf series synchronization for library {LibraryId}: selected {Selected}, updated {Updated}, skipped {Skipped}, failed {Failed}",
            request.LibraryId,
            selectedIds.Count,
            updated,
            skipped,
            failed);
        return new AudiobookshelfSeriesSyncResult(updated, skipped, failed, results);
    }

    private async Task<(AudiobookshelfLibrary Library, IReadOnlyList<AudiobookshelfLibraryItem> Items)>
        GetAudiobookshelfLibraryItemsAsync(
            string libraryId,
            CancellationToken cancellationToken)
    {
        var connection = await GetRequiredConnectionAsync();
        var libraries = await client.GetLibrariesAsync(
            connection.Url,
            connection.ApiKey!,
            cancellationToken);
        var library = libraries.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, libraryId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("Audiobookshelf library was not found.");
        var items = await client.GetLibraryItemsAsync(
            connection.Url,
            connection.ApiKey!,
            libraryId,
            cancellationToken);
        return (library, items);
    }

    private static IReadOnlyList<AudiobookSeriesMembershipPreview> GetCurrentSeries(Audiobook audiobook)
    {
        var memberships = audiobook.SeriesMemberships?
            .Where(membership => !string.IsNullOrWhiteSpace(membership.SeriesName))
            .OrderBy(membership => membership.SortOrder)
            .Select(membership => new AudiobookSeriesMembershipPreview(
                membership.SeriesName!.Trim(),
                NormalizePosition(membership.SeriesNumber)))
            .ToList();
        if (memberships is { Count: > 0 })
        {
            return memberships;
        }

        return string.IsNullOrWhiteSpace(audiobook.Series)
            ? []
            : [new AudiobookSeriesMembershipPreview(
                audiobook.Series.Trim(),
                NormalizePosition(audiobook.SeriesNumber))];
    }

    private static IReadOnlyList<AudiobookSeriesMembershipPreview> GetSourceSeries(
        AudiobookshelfMetadata metadata)
    {
        var memberships = metadata.SeriesMemberships?
            .Where(membership => !string.IsNullOrWhiteSpace(membership.Name))
            .Select(membership => new AudiobookSeriesMembershipPreview(
                membership.Name.Trim(),
                NormalizePosition(membership.Position)))
            .ToList();
        if (memberships is { Count: > 0 })
        {
            return memberships;
        }

        return string.IsNullOrWhiteSpace(metadata.Series)
            ? []
            : [new AudiobookSeriesMembershipPreview(
                metadata.Series.Trim(),
                NormalizePosition(metadata.SeriesNumber))];
    }

    private static bool AreSeriesEquivalent(
        IReadOnlyList<AudiobookSeriesMembershipPreview> current,
        IReadOnlyList<AudiobookSeriesMembershipPreview> source) =>
        current.Count == source.Count
        && current.Zip(source).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.Position, pair.Second.Position, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizePosition(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
