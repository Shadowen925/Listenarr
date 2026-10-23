namespace Listenarr.Application.Downloads.Queue
{
    public partial class DownloadQueueService
    {
        private static bool IsUnmatchedCompletedExternalDisplayCandidate(QueueItem queueItem)
        {
            return string.Equals(queueItem.Status, "completed", StringComparison.OrdinalIgnoreCase);
        }

        private static QueueItem ToDirectDownloadQueueItem(Download download)
        {
            var quality = download.Metadata != null && download.Metadata.TryGetValue("Quality", out var qualityObj)
                ? qualityObj?.ToString() ?? "Unknown"
                : "Unknown";

            return new QueueItem
            {
                Id = download.Id,
                Title = download.Title ?? "Unknown",
                Author = download.Artist,
                Quality = quality,
                Language = download.Language,
                Status = ToQueueStatus(download.Status),
                Progress = (double)download.Progress,
                Size = download.TotalSize,
                Downloaded = download.DownloadedSize,
                DownloadSpeed = 0,
                Eta = null,
                DownloadClient = "Direct Download",
                DownloadClientId = DirectDownloadMetadataKeys.ClientId,
                DownloadClientType = "ddl",
                AddedAt = download.StartedAt,
                CanPause = false,
                CanRemove = true,
                AudiobookId = download.AudiobookId,
                RemotePath = download.OriginalUrl,
                LocalPath = download.DownloadPath,
                ContentPath = string.IsNullOrWhiteSpace(download.FinalPath)
                    ? download.DownloadPath
                    : download.FinalPath,
                ErrorMessage = download.ImportBlockMessages?.LastOrDefault() ?? download.ErrorMessage,
                RecoveryState = download.GetMetadataString("Recovery.State")
                    ?? (download.Status == DownloadStatus.ImportBlocked ? "ImportFailed" : null),
                RecoveryReason = download.GetMetadataString("Recovery.Reason"),
                ActivityOperationIdentity = CreateActivityOperationIdentity(
                    download.DownloadClientId,
                    download.GetExternalId() ?? download.Id)
            };
        }

        private static string CreateActivityOperationIdentity(
            string? downloadClientId,
            string externalId) =>
            $"{downloadClientId}:{externalId}";

        internal static bool IsActivityIgnored(Download download) =>
            download.GetMetadataString("Activity.Ignored") is string value
            && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.OrdinalIgnoreCase));

        private static string ToQueueStatus(DownloadStatus status) => status switch
        {
            DownloadStatus.Queued => "queued",
            DownloadStatus.Downloading => "downloading",
            DownloadStatus.Paused => "paused",
            DownloadStatus.Completed => "completed",
            DownloadStatus.Processing => "processing",
            DownloadStatus.ImportPending => "importpending",
            DownloadStatus.ImportBlocked => "importblocked",
            DownloadStatus.Failed => "failed",
            DownloadStatus.Moved => "moved",
            _ => status.ToString().ToLowerInvariant()
        };
    }
}
