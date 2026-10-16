using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Cleanup
{
    /// <summary>
    /// Mode-aware queue removal. Unlike the legacy <see cref="DownloadRemovalWorkflow"/> it never
    /// infers success from a (category-filtered) queue listing: the client is addressed by the
    /// identity stored on the Listenarr record and any client failure is reported as a failure.
    /// Data deletion is delegated exclusively to the client API; Listenarr never touches the disk.
    /// </summary>
    public sealed class DownloadQueueRemovalService(
        IConfigurationService configurationService,
        IDownloadRepository downloadRepository,
        IDownloadClientGateway clientGateway,
        IRootFolderService rootFolderService,
        Listenarr.Application.Downloads.Stalls.DownloadEventRecorder events,
        ILogger<DownloadQueueRemovalService> logger)
    {
        public async Task<DownloadRemovalResult> RemoveAsync(
            string downloadId,
            string? downloadClientId,
            DownloadRemovalMode mode,
            CancellationToken ct = default)
        {
            var record = await ResolveRecordAsync(downloadId);
            if (record == null)
            {
                return DownloadRemovalResult.Failure(
                    DownloadRemovalErrorCodes.NotFound,
                    "This download is not tracked by Listenarr.");
            }

            if (!string.IsNullOrWhiteSpace(downloadClientId)
                && !string.IsNullOrWhiteSpace(record.DownloadClientId)
                && !string.Equals(downloadClientId, record.DownloadClientId, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadRemovalResult.Failure(
                    DownloadRemovalErrorCodes.ClientMismatch,
                    "The download client does not match the client that owns this download.");
            }

            if (mode == DownloadRemovalMode.ListenarrOnly
                || string.Equals(record.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                await downloadRepository.RemoveAsync(record.Id);
                await events.RecordAsync(DownloadEventTypes.ManualRemoval, record, message: "Removed from Listenarr only", ct: ct);
                return new DownloadRemovalResult(true, false, true, null, "Removed from Listenarr only. The download client was not contacted.");
            }

            var clientResult = await RemoveFromClientAsync(record, mode, ct);
            if (!clientResult.Success)
            {
                await events.RecordAsync(DownloadEventTypes.ManualRemoval, record, DownloadEventOutcomes.Failure, clientResult.Message, ct: ct);
                return clientResult;
            }

            await downloadRepository.RemoveAsync(record.Id);
            await events.RecordAsync(DownloadEventTypes.ManualRemoval, record, message: $"Removed with mode {mode}", ct: ct);
            return clientResult with { RemovedFromListenarr = true, Message = clientResult.Message + " Listenarr record removed." };
        }

        /// <summary>Removes the download from its owning client only; the Listenarr record is left untouched.</summary>
        public async Task<DownloadRemovalResult> RemoveFromClientAsync(Download record, DownloadRemovalMode mode, CancellationToken ct = default)
        {
            var client = await configurationService.GetDownloadClientConfigurationAsync(record.DownloadClientId);
            if (client == null || !client.IsEnabled)
            {
                return DownloadRemovalResult.Failure(
                    DownloadRemovalErrorCodes.ClientUnavailable,
                    "The owning download client is missing or disabled. Use 'Remove from Listenarr only'.");
            }

            var externalId = record.GetExternalId();
            if (string.IsNullOrWhiteSpace(externalId))
            {
                return DownloadRemovalResult.Failure(
                    DownloadRemovalErrorCodes.NoClientIdentity,
                    "Listenarr has no client-side identifier for this download, so it cannot be removed from the client safely. Use 'Remove from Listenarr only'.");
            }

            if (mode == DownloadRemovalMode.ClientDeleteData)
            {
                var unsafeReason = await GetDeleteDataRefusalAsync(record, client);
                if (unsafeReason != null)
                {
                    return DownloadRemovalResult.Failure(DownloadRemovalErrorCodes.UnsafeDelete, unsafeReason);
                }
            }

            try
            {
                var removed = await clientGateway.RemoveAsync(client, externalId, mode == DownloadRemovalMode.ClientDeleteData, ct);
                if (!removed)
                {
                    return DownloadRemovalResult.Failure(
                        DownloadRemovalErrorCodes.ClientFailed,
                        $"{client.Name} did not confirm the removal. Nothing was removed from Listenarr.");
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                if (LooksLikeMissingRemote(ex.Message))
                {
                    return DownloadRemovalResult.Failure(
                        DownloadRemovalErrorCodes.MissingRemote,
                        $"{client.Name} no longer has this item. You can remove the Listenarr record only.",
                        clientHadItem: false);
                }

                logger.LogWarning(ex, "Client removal failed for download {DownloadId} on {ClientName}", LogRedaction.SanitizeText(record.Id), LogRedaction.SanitizeText(client.Name));
                return DownloadRemovalResult.Failure(
                    DownloadRemovalErrorCodes.ClientFailed,
                    $"{client.Name} rejected the removal: {ex.Message}. Nothing was removed from Listenarr.");
            }

            return new DownloadRemovalResult(
                true,
                true,
                false,
                true,
                mode == DownloadRemovalMode.ClientDeleteData
                    ? "Removed from the client (partial data deleted by the client)."
                    : "Removed from the client (files kept).");
        }

        private async Task<Download?> ResolveRecordAsync(string downloadId)
        {
            var record = await downloadRepository.FindAsync(downloadId);
            if (record != null)
            {
                return record;
            }

            var all = await downloadRepository.GetAllAsync();
            return all.FirstOrDefault(d => string.Equals(d.GetExternalId(), downloadId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Returns why a delete-data request must be refused, or null when allowed.</summary>
        private async Task<string?> GetDeleteDataRefusalAsync(Download record, DownloadClientConfiguration client)
        {
            if (record.Status is DownloadStatus.Completed or DownloadStatus.Processing or DownloadStatus.Ready
                or DownloadStatus.Moved or DownloadStatus.ImportPending or DownloadStatus.ImportBlocked)
            {
                return "Data deletion is only offered for downloads that have not completed; this download may already be imported.";
            }

            var roots = (await rootFolderService.GetAllAsync()).Select(r => r.Path).Where(p => !string.IsNullOrWhiteSpace(p));
            var candidates = new[] { client.DownloadPath, record.DownloadPath, record.FinalPath }
                .Where(p => !string.IsNullOrWhiteSpace(p));

            foreach (var candidate in candidates)
            {
                foreach (var root in roots)
                {
                    if (IsWithinOrEqual(candidate!, root) || IsWithinOrEqual(root, candidate!))
                    {
                        return "The client download location overlaps a Listenarr library root, so data deletion is refused to protect the library.";
                    }
                }
            }

            return null;
        }

        internal static bool IsWithinOrEqual(string path, string root)
        {
            static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/') + "/";
            return Norm(path).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikeMissingRemote(string? message) =>
            message != null
            && (message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unknown torrent", StringComparison.OrdinalIgnoreCase)
                || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || message.Contains("InvalidTorrentError", StringComparison.OrdinalIgnoreCase));
    }
}
