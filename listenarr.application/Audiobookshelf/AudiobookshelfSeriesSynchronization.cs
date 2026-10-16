using System.Globalization;
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
                    [],
                    ListenarrSeriesCount: currentSeries.Count,
                    ProposedSeries: currentSeries,
                    FailureReason: "The Audiobookshelf library item no longer exists.",
                    Author: string.Join(", ", audiobook.Authors ?? []));
            }

            var sourceSeries = GetSourceSeries(audiobook, source.Metadata);
            var proposedSeries = BuildProposedSeries(audiobook, sourceSeries, out var conflict);
            var status = GetStatus(source, currentSeries, proposedSeries, conflict);
            return new AudiobookshelfSeriesSyncPreviewItem(
                itemId,
                audiobook.Id,
                audiobook.Title ?? string.Empty,
                status,
                currentSeries,
                sourceSeries,
                Math.Max(source.Metadata.SourceSeriesCount, sourceSeries.Count),
                sourceSeries.Count,
                currentSeries.Count,
                proposedSeries,
                GetFailureReason(source, status, conflict),
                string.Join(", ", audiobook.Authors ?? []));
        }).ToList();

        return new AudiobookshelfSeriesSyncPreview(library.Id, previews, Summarize(previews));
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
        if (preview.Items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal)
            .Count(itemId => selectedIds.Contains(itemId)) != selectedIds.Count)
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
                    previewItem.FailureReason ?? "This item is not eligible for series synchronization."));
                continue;
            }

            var sourceSeries = GetSourceSeries(audiobook, source.Metadata);
            var proposedSeries = BuildProposedSeries(audiobook, sourceSeries, out var conflict);
            if (conflict
                || GetMetadataState(source.Metadata) != AudiobookshelfSeriesMetadataState.Parsed
                || !AreSeriesEquivalent(previewItem.ProposedSeries ?? [], proposedSeries))
            {
                skipped++;
                results.Add(new AudiobookshelfSeriesSyncItemResult(
                    previewItem.ItemId,
                    previewItem.Title,
                    AudiobookshelfSeriesSyncOutcome.Skipped,
                    "Series metadata changed or became ambiguous after the preview was created."));
                continue;
            }

            if (AreSeriesEquivalent(GetCurrentSeries(audiobook), proposedSeries))
            {
                skipped++;
                results.Add(new AudiobookshelfSeriesSyncItemResult(
                    previewItem.ItemId,
                    previewItem.Title,
                    AudiobookshelfSeriesSyncOutcome.Skipped,
                    "Series metadata is already up to date."));
                continue;
            }

            var memberships = proposedSeries.Select((membership, index) =>
                new AudiobookSeriesMembership
                {
                    SeriesName = membership.Name,
                    SeriesNumber = membership.Position,
                    SeriesAsin = membership.SeriesAsin,
                    AudiobookshelfSeriesId = membership.AudiobookshelfSeriesId,
                    IsPrimary = index == 0,
                    SortOrder = index
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
                NormalizePosition(membership.SeriesNumber),
                AudiobookSeriesIdentity.GetId(
                    audiobook,
                    membership.SeriesName,
                    membership.SeriesAsin,
                    membership.AudiobookshelfSeriesId),
                membership.SeriesAsin,
                membership.AudiobookshelfSeriesId))
            .ToList();
        if (memberships is { Count: > 0 })
        {
            return memberships;
        }

        return string.IsNullOrWhiteSpace(audiobook.Series)
            ? []
            : [new AudiobookSeriesMembershipPreview(
                audiobook.Series.Trim(),
                NormalizePosition(audiobook.SeriesNumber),
                AudiobookSeriesIdentity.GetId(audiobook, audiobook.Series, null, null))];
    }

    private static IReadOnlyList<AudiobookSeriesMembershipPreview> GetSourceSeries(
        Audiobook audiobook,
        AudiobookshelfMetadata metadata)
    {
        var memberships = metadata.SeriesMemberships?
            .Where(membership => !string.IsNullOrWhiteSpace(membership.Name))
            .Select(membership => new AudiobookSeriesMembershipPreview(
                membership.Name.Trim(),
                NormalizePosition(membership.Position),
                AudiobookSeriesIdentity.GetId(
                    audiobook,
                    membership.Name,
                    membership.Asin,
                    membership.SourceId),
                membership.Asin,
                membership.SourceId))
            .ToList();
        if (memberships is { Count: > 0 })
        {
            return memberships;
        }

        return string.IsNullOrWhiteSpace(metadata.Series)
            ? []
            : [new AudiobookSeriesMembershipPreview(
                metadata.Series.Trim(),
                NormalizePosition(metadata.SeriesNumber),
                AudiobookSeriesIdentity.GetId(audiobook, metadata.Series, null, null))];
    }

    private static IReadOnlyList<AudiobookSeriesMembershipPreview> BuildProposedSeries(
        Audiobook audiobook,
        IReadOnlyList<AudiobookSeriesMembershipPreview> sourceSeries,
        out bool conflict)
    {
        conflict = false;
        var current = GetCurrentSeries(audiobook);
        var result = new List<AudiobookSeriesMembershipPreview>(sourceSeries.Count);
        foreach (var source in sourceSeries)
        {
            var existing = current
                .Where(membership =>
                    AudiobookSeriesIdentity.NormalizeName(membership.Name)
                    == AudiobookSeriesIdentity.NormalizeName(source.Name))
                .ToList();
            var existingAsins = existing
                .Select(membership => membership.SeriesAsin?.Trim().ToUpperInvariant())
                .Where(asin => !string.IsNullOrWhiteSpace(asin))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var existingSourceIds = existing
                .Select(membership => membership.AudiobookshelfSeriesId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (existingAsins.Count > 1
                || existingSourceIds.Count > 1
                || (source.SeriesAsin != null
                    && existingAsins.Count == 1
                    && !string.Equals(
                        source.SeriesAsin.Trim(),
                        existingAsins[0],
                        StringComparison.OrdinalIgnoreCase))
                || (source.AudiobookshelfSeriesId != null
                    && existingSourceIds.Count == 1
                    && !string.Equals(
                        source.AudiobookshelfSeriesId,
                        existingSourceIds[0],
                        StringComparison.Ordinal)))
            {
                conflict = true;
                result.Add(source);
                continue;
            }

            var asin = source.SeriesAsin ?? existingAsins.FirstOrDefault();
            var sourceId = source.AudiobookshelfSeriesId ?? existingSourceIds.FirstOrDefault();
            result.Add(source with
            {
                SeriesAsin = asin,
                AudiobookshelfSeriesId = sourceId,
                SeriesId = AudiobookSeriesIdentity.GetId(audiobook, source.Name, asin, sourceId)
            });
        }

        return result;
    }

    private static AudiobookshelfSeriesSyncStatus GetStatus(
        AudiobookshelfLibraryItem source,
        IReadOnlyList<AudiobookSeriesMembershipPreview> current,
        IReadOnlyList<AudiobookSeriesMembershipPreview> proposed,
        bool conflict)
    {
        var state = GetMetadataState(source.Metadata);
        if (source.SourceUnavailableReason != null
            && state == AudiobookshelfSeriesMetadataState.NotProvided)
        {
            return AudiobookshelfSeriesSyncStatus.SourceUnavailable;
        }

        if (state == AudiobookshelfSeriesMetadataState.ParseError)
        {
            return AudiobookshelfSeriesSyncStatus.ParseError;
        }

        if (state is AudiobookshelfSeriesMetadataState.NoSeries or AudiobookshelfSeriesMetadataState.NotProvided)
        {
            return current.Count == 0
                ? AudiobookshelfSeriesSyncStatus.NoSeries
                : AudiobookshelfSeriesSyncStatus.Conflict;
        }

        if (conflict)
        {
            return AudiobookshelfSeriesSyncStatus.Conflict;
        }

        return AreSeriesEquivalent(current, proposed)
            ? AudiobookshelfSeriesSyncStatus.UpToDate
            : AudiobookshelfSeriesSyncStatus.NeedsUpdate;
    }

    private static AudiobookshelfSeriesMetadataState GetMetadataState(AudiobookshelfMetadata metadata)
    {
        if (metadata.SeriesMetadataState != AudiobookshelfSeriesMetadataState.NotProvided)
        {
            return metadata.SeriesMetadataState;
        }

        return metadata.SeriesMemberships?.Count > 0 || !string.IsNullOrWhiteSpace(metadata.Series)
            ? AudiobookshelfSeriesMetadataState.Parsed
            : AudiobookshelfSeriesMetadataState.NotProvided;
    }

    private static string? GetFailureReason(
        AudiobookshelfLibraryItem source,
        AudiobookshelfSeriesSyncStatus status,
        bool conflict) =>
        status switch
        {
            AudiobookshelfSeriesSyncStatus.SourceUnavailable =>
                source.SourceUnavailableReason ?? "Series metadata could not be retrieved from Audiobookshelf.",
            AudiobookshelfSeriesSyncStatus.ParseError =>
                source.Metadata.SeriesMetadataFailureReason
                ?? "Audiobookshelf series metadata could not be interpreted.",
            AudiobookshelfSeriesSyncStatus.Conflict when conflict =>
                "The existing Listenarr series identity is ambiguous or incompatible with Audiobookshelf.",
            AudiobookshelfSeriesSyncStatus.Conflict =>
                "Audiobookshelf reports no series, but Listenarr currently has series memberships.",
            _ => null
        };

    private static bool AreSeriesEquivalent(
        IReadOnlyList<AudiobookSeriesMembershipPreview> current,
        IReadOnlyList<AudiobookSeriesMembershipPreview> source) =>
        current.Count == source.Count
        && current.Zip(source).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.Position, pair.Second.Position, StringComparison.OrdinalIgnoreCase)
            && (pair.Second.SeriesId == null
                || string.Equals(pair.First.SeriesId, pair.Second.SeriesId, StringComparison.Ordinal))
            && (pair.Second.SeriesAsin == null
                || string.Equals(pair.First.SeriesAsin, pair.Second.SeriesAsin, StringComparison.OrdinalIgnoreCase))
            && (pair.Second.AudiobookshelfSeriesId == null
                || string.Equals(
                    pair.First.AudiobookshelfSeriesId,
                    pair.Second.AudiobookshelfSeriesId,
                    StringComparison.Ordinal)));

    private static string? NormalizePosition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return decimal.TryParse(
            trimmed,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number.ToString("0.############################", CultureInfo.InvariantCulture)
            : trimmed;
    }

    private static AudiobookshelfSeriesSyncSummary Summarize(
        IReadOnlyList<AudiobookshelfSeriesSyncPreviewItem> items) =>
        new(
            items.Count(item => item.Status == AudiobookshelfSeriesSyncStatus.NeedsUpdate),
            items.Count(item => item.Status == AudiobookshelfSeriesSyncStatus.UpToDate),
            items.Count(item => item.Status == AudiobookshelfSeriesSyncStatus.NoSeries),
            items.Count(item => item.Status == AudiobookshelfSeriesSyncStatus.SourceUnavailable),
            items.Count(item => item.Status == AudiobookshelfSeriesSyncStatus.ParseError),
            items.Count(item => item.Status == AudiobookshelfSeriesSyncStatus.Conflict),
            items.Count(item => item.Status == AudiobookshelfSeriesSyncStatus.MissingInAudiobookshelf));
}
