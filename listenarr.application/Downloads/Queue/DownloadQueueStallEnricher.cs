using Listenarr.Application.Downloads.Stalls;

namespace Listenarr.Application.Downloads.Queue
{
    /// <summary>
    /// Adds stall/replacement diagnostics from tracked Download records to the queue snapshot and surfaces
    /// tracked active downloads that the client did not report, so they can still be inspected and removed.
    /// </summary>
    internal static class DownloadQueueStallEnricher
    {
        public static void Apply(
            List<QueueItem> items,
            IEnumerable<Download> trackedDownloads,
            IReadOnlyCollection<DownloadClientConfiguration> enabledClients,
            ISet<string> clientsWithLiveQueue)
        {
            var byId = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var download in trackedDownloads)
            {
                if (string.Equals(download.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase)
                    || download.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused))
                {
                    continue;
                }

                if (byId.TryGetValue(download.Id, out var existing))
                {
                    Enrich(existing, download);
                    continue;
                }

                // Only surface a tracked download as missing once the stall tracker has confirmed the absence persisted.
                if (StallTracker.GetState(download) != nameof(StallVerdict.MissingFromClient))
                {
                    continue;
                }

                var client = enabledClients.FirstOrDefault(c => string.Equals(c.Id, download.DownloadClientId, StringComparison.OrdinalIgnoreCase));
                if (client == null || !clientsWithLiveQueue.Contains(client.Id))
                {
                    continue;
                }

                var synthetic = new QueueItem
                {
                    Id = download.Id,
                    Title = download.Title ?? "Unknown",
                    Author = download.Artist,
                    Quality = download.Metadata.TryGetValue("Quality", out var quality) ? quality?.ToString() ?? "Unknown" : "Unknown",
                    Language = download.Language,
                    Status = download.Status.ToString().ToLowerInvariant(),
                    Progress = (double)download.Progress,
                    Size = download.TotalSize,
                    Downloaded = download.DownloadedSize,
                    DownloadClient = client.Name ?? client.Id,
                    DownloadClientId = client.Id,
                    DownloadClientType = client.Type?.ToLowerInvariant() ?? "external",
                    AddedAt = download.StartedAt,
                    CanPause = false,
                    CanRemove = true,
                    AudiobookId = download.AudiobookId,
                    MissingFromClient = true,
                    ErrorMessage = "The download client did not report this download.",
                };
                Enrich(synthetic, download);
                items.Add(synthetic);
            }
        }

        private static void Enrich(QueueItem item, Download download)
        {
            var state = StallTracker.GetState(download);
            item.StallState = state;
            item.StallReason = download.GetMetadataString(StallMetadataKeys.Reason);
            item.StallSince = StallTracker.ReadDate(download, StallMetadataKeys.Since);
            item.LastProgressAt = StallTracker.ReadDate(download, StallMetadataKeys.LastProgressAt);
            item.ReplacementAttempts = StallTracker.GetReplacementAttempts(download);
            item.ReplacementState = download.GetMetadataString(StallMetadataKeys.ReplacementState);
            item.ReplacementError = download.GetMetadataString(StallMetadataKeys.ReplacementError);
            item.MissingFromClient = item.MissingFromClient || state == nameof(StallVerdict.MissingFromClient);
        }
    }
}
