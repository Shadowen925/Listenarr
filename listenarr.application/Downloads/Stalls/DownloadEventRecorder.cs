using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Stalls
{
    /// <summary>
    /// Writes bounded download lifecycle events. Recording is best effort: an audit failure must never
    /// change the outcome of the operation being audited.
    /// </summary>
    public sealed class DownloadEventRecorder(IDownloadEventRepository repository, ILogger<DownloadEventRecorder> logger)
    {
        private const int MaxMessageLength = 500;
        private static readonly Regex UrlPattern = new(@"[a-z][a-z0-9+.\-]*://\S+|magnet:\?\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public Task RecordAsync(
            string eventType,
            Download? download,
            string outcome = DownloadEventOutcomes.Success,
            string? message = null,
            string? releaseId = null,
            string? title = null,
            CancellationToken ct = default)
            => RecordCoreAsync(new DownloadEvent
            {
                Timestamp = DateTime.UtcNow,
                EventType = eventType,
                DownloadId = download?.Id,
                AudiobookId = download?.AudiobookId,
                ClientId = download?.DownloadClientId,
                ExternalId = download?.GetExternalId(),
                ReleaseId = releaseId,
                Title = Truncate(title ?? download?.Title, 500),
                Outcome = outcome,
                Message = Sanitize(message),
            }, ct);

        public static string? Sanitize(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            return Truncate(UrlPattern.Replace(message, "[url]"), MaxMessageLength);
        }

        private static string? Truncate(string? value, int max)
            => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

        private async Task RecordCoreAsync(DownloadEvent downloadEvent, CancellationToken ct)
        {
            try
            {
                await repository.AddAsync(downloadEvent, ct);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(ex, "Could not record download event {EventType}", downloadEvent.EventType);
            }
        }
    }
}
