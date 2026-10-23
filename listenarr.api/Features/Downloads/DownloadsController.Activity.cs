using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Downloads;

public partial class DownloadsController
{
    [HttpGet("ignored")]
    public async Task<ActionResult> GetIgnoredDownloads()
    {
        var downloads = await _downloadRepository.GetAllAsync();
        var ignored = downloads
            .Where(download =>
                IsTrue(download.GetMetadataString("Activity.Ignored"))
                || (download.Status == DownloadStatus.Moved
                    && IsTrue(download.GetMetadataString(Download.SourceRetainedMetadataKey))))
            .OrderByDescending(download => download.LastImportedAt ?? download.StartedAt)
            .Select(download => new
            {
                download.Id,
                download.AudiobookId,
                download.Title,
                Status = download.Status.ToString(),
                download.DownloadClientId,
                ExternalId = download.GetExternalId(),
                Ignored = IsTrue(download.GetMetadataString("Activity.Ignored")),
                SourceRetained = IsTrue(download.GetMetadataString(Download.SourceRetainedMetadataKey)),
                IgnoredAt = download.GetMetadataString("Activity.IgnoredAt"),
                IgnoreReason = download.GetMetadataString("Activity.IgnoreReason")
            })
            .ToArray();
        return Ok(ignored);
    }

    [HttpPost("{id}/ignore")]
    public async Task<ActionResult> IgnoreDownload(string id)
    {
        var download = await _downloadRepository.FindAsync(id);
        if (download == null)
        {
            return NotFound(new { error = "Download not found", id });
        }

        var jobs = await _downloadProcessingJobRepository.GetByDownloadIdAsync(id);
        if (jobs.Any(job =>
            job.Status is ProcessingJobStatus.Pending
                or ProcessingJobStatus.Processing
                or ProcessingJobStatus.Retry))
        {
            return Conflict(new { error = "An active import job must finish before this download can be ignored." });
        }

        if (download.Status is not (
            DownloadStatus.Completed
                or DownloadStatus.Failed
                or DownloadStatus.ImportBlocked
                or DownloadStatus.ImportPending
                or DownloadStatus.Moved))
        {
            return Conflict(new { error = "Only completed or failed download operations can be ignored." });
        }

        download.SetMetadata("Activity.Ignored", true);
        download.SetMetadata("Activity.IgnoredAt", DateTime.UtcNow);
        download.SetMetadata(
            "Activity.IgnoreReason",
            "Excluded from repeated import processing by the user.");
        if (download.Status is DownloadStatus.Completed or DownloadStatus.ImportPending)
        {
            download.SetStatus(DownloadStatus.ImportBlocked);
            download.SetMetadata("Recovery.State", "Ignored");
        }

        await _downloadService.UpdateAsync(download);
        return Ok(new { id = download.Id, externalId = download.GetExternalId() });
    }

    [HttpPost("{id}/restore-monitoring")]
    public async Task<ActionResult> RestoreDownloadMonitoring(string id)
    {
        var download = await _downloadRepository.FindAsync(id);
        if (download == null)
        {
            return NotFound(new { error = "Download not found", id });
        }

        var wasIgnored = IsTrue(download.GetMetadataString("Activity.Ignored"));
        download.Metadata.Remove("Activity.Ignored");
        download.Metadata.Remove("Activity.IgnoredAt");
        download.Metadata.Remove("Activity.IgnoreReason");
        var shouldRetryImport = wasIgnored
            && download.Status == DownloadStatus.ImportBlocked
            && string.Equals(
                download.GetMetadataString("Recovery.State"),
                "Ignored",
                StringComparison.OrdinalIgnoreCase);
        if (shouldRetryImport)
        {
            download.Metadata.Remove("Recovery.State");
            download.Unblock();
        }

        await _downloadService.UpdateAsync(download);
        if (shouldRetryImport)
        {
            if (_downloadProcessingJobService == null)
            {
                throw new InvalidOperationException("Download processing is not available.");
            }

            var jobId = await _downloadProcessingJobService.RetryAsync(download);
            return Ok(new { id = download.Id, jobId, status = download.Status.ToString() });
        }

        return Ok(new { id = download.Id, status = download.Status.ToString() });
    }

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;
}
