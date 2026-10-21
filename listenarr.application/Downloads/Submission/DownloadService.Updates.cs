using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Submission;

public partial class DownloadService
{
    public async Task UpdateAsync(Download download)
    {
        var previous = await downloadRepository.GetByIdAsync(download.Id);
        if (previous is null)
        {
            logger.LogWarning("Skipping update for unknown download {DownloadId}", LogRedaction.SanitizeText(download.Id));
            return;
        }

        await downloadRepository.UpdateAsync(download);
        if (download.AudiobookId.HasValue)
        {
            var operationState = download.Status switch
            {
                DownloadStatus.Queued => QuickAddOperationState.Queued,
                DownloadStatus.Downloading => QuickAddOperationState.Downloading,
                DownloadStatus.Completed or DownloadStatus.Ready => QuickAddOperationState.DownloadCompleted,
                DownloadStatus.ImportPending => QuickAddOperationState.ImportPending,
                DownloadStatus.Processing => QuickAddOperationState.Importing,
                DownloadStatus.Failed => QuickAddOperationState.FailedNeedsAttention,
                _ => (QuickAddOperationState?)null
            };
            if (operationState.HasValue)
            {
                await quickAddOperations.AdvanceAsync(
                    download.AudiobookId.Value,
                    operationState.Value,
                    downloadId: download.Id,
                    downloadClientId: download.DownloadClientId,
                    failureReason: operationState == QuickAddOperationState.FailedNeedsAttention
                        ? download.ErrorMessage ?? "The download client reported this download as failed."
                        : download.ErrorMessage);
            }
        }

        switch (previous.Status, download.Status)
        {
            case var (old, next) when old == next:
                return;
            case (_, DownloadStatus.Moved):
                await notificationService.OnDownloadImportedAsync(download);
                return;
            case (_, DownloadStatus.Failed):
            case (_, DownloadStatus.ImportBlocked):
                await notificationService.OnDownloadFailedAsync(download);
                return;
            default:
                return;
        }
    }
}
