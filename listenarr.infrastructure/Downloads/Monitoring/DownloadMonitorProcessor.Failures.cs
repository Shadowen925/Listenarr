using Listenarr.Application.Downloads.Stalls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    public partial class DownloadMonitorProcessor
    {
        private async Task OnDownloadFailed(
            Download download,
            DownloadClientConfiguration client,
            string errorMessage,
            CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var downloadHistoryService = scope.ServiceProvider.GetRequiredService<IDownloadHistoryService>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetApplicationSettingsAsync();
            var releaseRejections = scope.ServiceProvider.GetRequiredService<IReleaseRejectionRepository>();

            await downloadHistoryService.RecordDownloadFailedAsync(
                download.Id,
                download.DownloadClientId,
                download.Title ?? "Unknown",
                errorMessage);

            if (download.AudiobookId is int failedAudiobookId)
            {
                foreach (var releaseKey in ReleaseRejectionSet.GetDownloadReleaseKeys(download))
                {
                    try
                    {
                        await releaseRejections.AddAsync(new ReleaseRejection
                        {
                            AudiobookId = failedAudiobookId,
                            ReleaseKey = releaseKey,
                            ReleaseTitle = download.Title,
                            Reason = errorMessage,
                            Source = "DownloadFailure",
                            CreatedAt = timeProvider.GetUtcNow().UtcDateTime
                        }, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                    {
                        logger.LogWarning(
                            ex,
                            "Could not persist rejected release {ReleaseKey} for failed download {DownloadId}",
                            releaseKey,
                            LogRedaction.SanitizeText(download.Id));
                    }
                }
            }

            if (!settings.FailedDownloadHandlingEnabled)
            {
                return;
            }

            var clientItemId = download.GetExternalId();
            if (!string.IsNullOrWhiteSpace(clientItemId) &&
                ShouldRemoveFailedClientItem(client))
            {
                await downloadClientGateway.RemoveAsync(client, clientItemId, deleteFiles: false, cancellationToken);
            }

            if (settings.FailedDownloadAutoSearch && download.AudiobookId.HasValue)
            {
                if (ShouldSuppressFailedDownloadAutoSearch(client, download, errorMessage))
                {
                    logger.LogInformation(
                        "Skipping immediate auto-search for failed NZBGet download {DownloadId}; client failure requires user action or manual retry",
                        download.Id);
                    return;
                }

                try
                {
                    var audiobook = await audiobookRepository.GetByIdAsync(download.AudiobookId.Value);
                    if (audiobook != null && audiobook.Monitored)
                    {
                        var automaticSearchProcessor =
                            scope.ServiceProvider.GetRequiredService<IAutomaticSearchProcessor>();
                        await automaticSearchProcessor.SearchAudiobookAsync(
                            download.AudiobookId.Value,
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogError(ex, "Failed to auto-search after failed download {DownloadId}", download.Id);
                }
            }
        }

        internal static bool ShouldRemoveFailedClientItem(DownloadClientConfiguration client) =>
            !string.Equals(client.Type, "nzbget", StringComparison.OrdinalIgnoreCase);

        internal static bool ShouldSuppressFailedDownloadAutoSearch(
            DownloadClientConfiguration client,
            Download download,
            string errorMessage)
        {
            if (!string.Equals(client.Type, "nzbget", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var clientFailureReason = download.GetMetadataString("ClientFailureReason") ?? errorMessage;
            return NzbgetFailureMessageMapper.IsMoveOrPostProcessingFailure(clientFailureReason);
        }
    }
}
