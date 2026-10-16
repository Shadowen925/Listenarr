using Listenarr.Application.Downloads.Stalls;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Downloads.Monitoring;

internal static class DownloadMonitorEventRecorder
{
    public static async Task RecordTransitionEventsAsync(
        IServiceProvider services,
        Download current,
        Download? previous,
        CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            return;
        }

        var recorder = services.GetRequiredService<DownloadEventRecorder>();
        if (StallTracker.GetState(current) == nameof(StallVerdict.Stalled)
            && StallTracker.GetState(previous) != nameof(StallVerdict.Stalled))
        {
            await recorder.RecordAsync(
                DownloadEventTypes.Stalled,
                current,
                DownloadEventOutcomes.Info,
                current.GetMetadataString(StallMetadataKeys.Reason),
                ct: cancellationToken);
        }

        if (current.Status == DownloadStatus.Completed && previous.Status != DownloadStatus.Completed)
        {
            await recorder.RecordAsync(DownloadEventTypes.Completed, current, ct: cancellationToken);
        }
    }
}
