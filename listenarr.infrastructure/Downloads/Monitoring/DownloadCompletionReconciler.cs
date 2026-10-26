using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    internal sealed partial class DownloadCompletionReconciler(
        IRemotePathMappingService remotePathMappingService,
        IFileSystem fileSystem,
        IMetadataService metadataService,
        IAudiobookRepository audiobookRepository,
        IMemoryCache metadataCache,
        TimeProvider timeProvider,
        ILogger<DownloadCompletionReconciler> logger)
    {
        private const string StabilityKey = "Recovery.Stability";
        private const string StateKey = "Recovery.State";
        private const string ReasonKey = "Recovery.Reason";
        private const string ClientPresenceKey = "Recovery.ClientPresence";
        private const string ClientContentPathKey = "ClientContentPath";

        private readonly DownloadCompletionDirectoryScanner _directoryScanner =
            new(remotePathMappingService, fileSystem, logger);

        public async Task<HashSet<string>> ReconcileAsync(
            DownloadClientConfiguration client,
            IReadOnlyCollection<Download> missingDownloads,
            ApplicationSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!settings.EnableCompletedDownloadReconciliation || missingDownloads.Count == 0)
            {
                return recovered;
            }

            var recoverableDownloads = missingDownloads.Where(CanRecover).ToList();
            foreach (var download in recoverableDownloads)
            {
                download.SetMetadata(ClientPresenceKey, "MissingFromClient");
                SetRecovery(
                    download,
                    "MissingFromClient",
                    "The download client did not report this tracked item in its latest successful snapshot.");
            }

            if (recoverableDownloads.Count == 0)
            {
                return recovered;
            }

            var mappings = await remotePathMappingService.GetPathMappingByClientAsync(client);
            cancellationToken.ThrowIfCancellationRequested();
            if (mappings.Count == 0)
            {
                foreach (var download in recoverableDownloads)
                {
                    SetRecovery(
                        download,
                        "RecoveryPending",
                        "No client-specific remote path mapping is configured for completed-download discovery.");
                }

                logger.LogWarning(
                    "Skipping completion reconciliation for client {ClientId}: no client-specific completed-download path mappings are configured",
                    client.Id);
                return recovered;
            }

            var (authorizedRoots, failureReason) = await _directoryScanner.ResolveAuthorizedRootsAsync(
                client,
                mappings,
                cancellationToken);
            if (authorizedRoots.Count == 0)
            {
                foreach (var download in recoverableDownloads)
                {
                    SetRecovery(download, "RecoveryPending", failureReason);
                }

                logger.LogWarning(
                    "Skipping completion reconciliation for client {ClientId}: {Reason}",
                    client.Id,
                    failureReason);
                return recovered;
            }

            var metadataByPath = new Dictionary<string, AudioMetadata?>(StringComparer.Ordinal);
            DownloadCompletionDirectoryScanResult? broadScan = null;
            foreach (var download in recoverableDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var audiobook = download.AudiobookId.HasValue
                    ? await audiobookRepository.GetByIdAsync(download.AudiobookId.Value)
                    : null;
                cancellationToken.ThrowIfCancellationRequested();
                if (audiobook == null || HasConflictingAsin(download, audiobook))
                {
                    SetRecovery(download, "RecoveryPending", "The tracked audiobook identity could not be verified.");
                    continue;
                }

                List<StableCandidateFile>? matchedFiles = null;
                // DownloadPath is copied from the client's shared configuration at submission,
                // so only item-specific paths may narrow discovery.
                var (specificDirectory, mappingUnavailable) = await _directoryScanner.ResolveDownloadSpecificDirectoryAsync(
                    client,
                    authorizedRoots,
                    [
                        download.GetMetadataString(ClientContentPathKey),
                        download.FinalPath
                    ],
                    cancellationToken);
                if (mappingUnavailable)
                {
                    SetRecovery(
                        download,
                        "RecoveryPending",
                        "A persisted download path is owned by an unavailable client mapping; broader discovery was not attempted.");
                    continue;
                }

                if (specificDirectory != null)
                {
                    var specificScan = _directoryScanner.EnumerateAudioFiles([specificDirectory], cancellationToken);
                    var specificMatches = await FindMatchingFilesAsync(
                        specificScan.Files,
                        metadataByPath,
                        audiobook,
                        cancellationToken);
                    if (!specificScan.IsComplete || !specificMatches.IsComplete)
                    {
                        SetRecovery(
                            download,
                            "RecoveryPending",
                            "The verified download-specific directory could not be scanned completely within the configured limits.");
                        continue;
                    }

                    if (specificMatches.MatchingGroups > 1)
                    {
                        SetRecovery(
                            download,
                            "RecoveryPending",
                            "Multiple matching directories were found beneath the verified download-specific directory.");
                        continue;
                    }

                    matchedFiles = specificMatches.Files;
                }

                if (matchedFiles == null)
                {
                    broadScan ??= _directoryScanner.EnumerateAudioFiles(authorizedRoots, cancellationToken);
                    var broadMatches = await FindMatchingFilesAsync(
                        broadScan.Files,
                        metadataByPath,
                        audiobook,
                        cancellationToken);
                    if (!broadScan.IsComplete || !broadMatches.IsComplete)
                    {
                        SetRecovery(
                            download,
                            "RecoveryPending",
                            "Authorized completed-download discovery was incomplete because a directory was inaccessible or a scan limit was reached.");
                        continue;
                    }

                    if (broadMatches.MatchingGroups > 1)
                    {
                        SetRecovery(
                            download,
                            "RecoveryPending",
                            "Multiple authorized directories contain matching files; recovery was left ambiguous.");
                        continue;
                    }

                    matchedFiles = broadMatches.Files;
                }

                if (matchedFiles == null || matchedFiles.Count == 0)
                {
                    SetRecovery(
                        download,
                        "RecoveryPending",
                        "No confident completed-file match was found in this client's authorized mapped directories.");
                    continue;
                }

                if (!HasCompletionEvidence(download, matchedFiles, audiobook))
                {
                    SetRecovery(
                        download,
                        "RecoveryPending",
                        "Matching files are present, but completion cannot be confirmed by client state, size, or embedded audiobook identity.");
                    continue;
                }

                if (!AreFilesStable(download, matchedFiles, settings.CompletedDownloadMinimumStabilitySeconds))
                {
                    continue;
                }

                var filePaths = matchedFiles.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal).ToList();
                download.DownloadPath = Path.GetDirectoryName(filePaths[0]) ?? authorizedRoots[0];
                download.SetMetadata("ClientContentPath", download.DownloadPath);
                download.SetMetadata(
                    Download.RecoveredSourceFilesMetadataKey,
                    JsonSerializer.Serialize(filePaths));
                download.SetMetadata(StateKey, "ImportPending");
                download.SetMetadata(ReasonKey, "Matched files were stable, readable, and associated with the tracked audiobook.");
                download.SetMetadata(ClientPresenceKey, "MissingFromClient");

                if (settings.AutomaticImportReconciledDownloads)
                {
                    download.CompletedAt ??= timeProvider.GetUtcNow().UtcDateTime;
                    download.Completed();
                    recovered.Add(download.Id);
                    logger.LogInformation(
                        "Recovered missed completion for download {DownloadId} using {FileCount} verified source file(s)",
                        download.Id,
                        filePaths.Count);
                }
                else
                {
                    if (download.Status != DownloadStatus.ImportPending)
                    {
                        download.Importing();
                    }

                    logger.LogInformation(
                        "Recovered download {DownloadId} is awaiting manual import",
                        download.Id);
                }
            }

            return recovered;
        }

        private bool AreFilesStable(
            Download download,
            IReadOnlyCollection<StableCandidateFile> files,
            int minimumStabilitySeconds)
        {
            if (files.Any(file => file.Length <= 0))
            {
                SetRecovery(download, "RecoveryPending", "A recovered audio file is empty.");
                return false;
            }

            var signatureText = string.Join(
                "\n",
                files.OrderBy(file => file.Path, StringComparer.Ordinal)
                    .Select(file => $"{file.Path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"));
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureText)));
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var previousSnapshot = download.GetMetadataString(StabilityKey);
            StabilitySnapshot? snapshot = null;
            if (!string.IsNullOrWhiteSpace(previousSnapshot))
            {
                snapshot = JsonSerializer.Deserialize<StabilitySnapshot>(previousSnapshot);
            }

            if (snapshot == null || !string.Equals(snapshot.Signature, signature, StringComparison.Ordinal))
            {
                download.SetMetadata(StabilityKey, JsonSerializer.Serialize(new StabilitySnapshot(signature, now)));
                SetRecovery(download, "RecoveryPending", "A matching file set was found and is being checked for stability.");
                return false;
            }

            return now - snapshot.FirstObservedUtc >= TimeSpan.FromSeconds(Math.Clamp(minimumStabilitySeconds, 15, 3600));
        }

        private static bool HasCompletionEvidence(
            Download download,
            IReadOnlyCollection<StableCandidateFile> files,
            Audiobook audiobook)
        {
            var clientState = download.GetMetadataString("ClientState")?.Trim().ToLowerInvariant();
            if (clientState is "completed" or "success" or "seeding" or "finished")
            {
                return true;
            }

            if (download.ExpectedFileSize is > 0)
            {
                return files.Sum(file => file.Length) >= download.ExpectedFileSize.Value;
            }

            return files.All(file => HasStrongMetadataIdentity(file.Metadata, audiobook));
        }

        private static bool HasStrongMetadataIdentity(AudioMetadata? metadata, Audiobook audiobook)
        {
            if (!string.IsNullOrWhiteSpace(metadata?.Asin)
                && !string.IsNullOrWhiteSpace(audiobook.Asin)
                && string.Equals(metadata.Asin, audiobook.Asin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var title = Normalize(audiobook.Title);
            var fileTitle = Normalize(FirstNonEmpty(metadata?.Album, metadata?.Title));
            var artist = Normalize(FirstNonEmpty(metadata?.AlbumArtist, metadata?.Artist));
            return !string.IsNullOrWhiteSpace(title)
                && string.Equals(fileTitle, title, StringComparison.OrdinalIgnoreCase)
                && audiobook.Authors?.Where(author => !string.IsNullOrWhiteSpace(author))
                    .Select(Normalize)
                    .Any(author => string.Equals(artist, author, StringComparison.OrdinalIgnoreCase)
                        || artist.Contains(author, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static bool CanRecover(Download download)
        {
            if (download.Status is not (DownloadStatus.Queued
                or DownloadStatus.Downloading
                or DownloadStatus.Completed
                or DownloadStatus.ImportPending))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(download.GetMetadataString(Download.RecoveredSourceFilesMetadataKey)))
            {
                return false;
            }

            var state = download.GetMetadataString("ClientState")?.Trim().ToLowerInvariant();
            return state is not ("paused" or "stoppeddl" or "stoppedup");
        }

        private static bool HasConflictingAsin(Download download, Audiobook audiobook) =>
            !string.IsNullOrWhiteSpace(download.Asin)
            && !string.IsNullOrWhiteSpace(audiobook.Asin)
            && !string.Equals(download.Asin, audiobook.Asin, StringComparison.OrdinalIgnoreCase);

        private static void SetRecovery(Download download, string state, string reason)
        {
            download.SetMetadata(StateKey, state);
            download.SetMetadata(ReasonKey, reason);
        }

        private sealed record StabilitySnapshot(string Signature, DateTime FirstObservedUtc);

        private sealed record MatchingFileSearchResult(
            List<StableCandidateFile>? Files,
            int MatchingGroups,
            bool IsComplete);

        private sealed record StableCandidateFile(
            string Path,
            long Length,
            DateTime LastWriteTimeUtc,
            AudioMetadata? Metadata);
    }
}
