using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    internal sealed class DownloadCompletionReconciler(
        IRemotePathMappingService remotePathMappingService,
        IFileSystem fileSystem,
        IMetadataService metadataService,
        IAudiobookRepository audiobookRepository,
        TimeProvider timeProvider,
        ILogger<DownloadCompletionReconciler> logger)
    {
        private const int MaximumScannedFiles = 1000;
        private const int MaximumDirectoryDepth = 5;
        private const string StabilityKey = "Recovery.Stability";
        private const string StateKey = "Recovery.State";
        private const string ReasonKey = "Recovery.Reason";

        public async Task<HashSet<string>> ReconcileAsync(
            DownloadClientConfiguration client,
            IReadOnlyCollection<Download> missingDownloads,
            ApplicationSettings settings,
            CancellationToken cancellationToken)
        {
            var recovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!settings.EnableCompletedDownloadReconciliation || missingDownloads.Count == 0)
            {
                return recovered;
            }

            if (string.IsNullOrWhiteSpace(client.DownloadPath))
            {
                logger.LogWarning(
                    "Skipping completion reconciliation for client {ClientId}: no authorized download directory is configured",
                    client.Id);
                return recovered;
            }

            string root;
            try
            {
                root = await remotePathMappingService.TranslatePathAsync(client, client.DownloadPath);
            }
            catch (InvalidOperationException exception)
            {
                foreach (var download in missingDownloads)
                {
                    SetAttention(download, "The configured completed directory could not be mapped to a local path.");
                }

                logger.LogWarning(
                    exception,
                    "Skipping completion reconciliation for client {ClientId}: its configured directory mapping is unavailable",
                    client.Id);
                return recovered;
            }

            if (string.IsNullOrWhiteSpace(root)
                || !fileSystem.DirectoryExists(root)
                || fileSystem.IsReparsePoint(root))
            {
                foreach (var download in missingDownloads)
                {
                    SetAttention(download, "The configured completed directory is unavailable to Listenarr.");
                }

                logger.LogWarning(
                    "Skipping completion reconciliation for client {ClientId}: configured download directory is unavailable",
                    client.Id);
                return recovered;
            }

            var candidateFiles = EnumerateAudioFiles(root);
            var metadataByPath = new Dictionary<string, AudioMetadata?>(StringComparer.Ordinal);
            foreach (var download in missingDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanRecover(download))
                {
                    continue;
                }

                var audiobook = download.AudiobookId.HasValue
                    ? await audiobookRepository.GetByIdAsync(download.AudiobookId.Value)
                    : null;
                if (audiobook == null || HasConflictingAsin(download, audiobook))
                {
                    SetAttention(download, "The tracked audiobook identity could not be verified.");
                    continue;
                }

                var matchedFiles = await FindMatchingFilesAsync(
                    candidateFiles,
                    metadataByPath,
                    audiobook,
                    cancellationToken);
                if (matchedFiles.Count == 0)
                {
                    SetAttention(download, "No confident completed-file match was found in the configured client directory.");
                    continue;
                }

                if (!HasCompletionEvidence(download, matchedFiles))
                {
                    SetAttention(download, "Files are present, but completion cannot be confirmed from client or size evidence.");
                    continue;
                }

                if (!AreFilesStable(download, matchedFiles, settings.CompletedDownloadMinimumStabilitySeconds))
                {
                    continue;
                }

                var filePaths = matchedFiles.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal).ToList();
                download.DownloadPath = Path.GetDirectoryName(filePaths[0]) ?? root;
                download.SetMetadata("ClientContentPath", download.DownloadPath);
                download.SetMetadata(
                    Download.RecoveredSourceFilesMetadataKey,
                    JsonSerializer.Serialize(filePaths));
                download.SetMetadata(StateKey, settings.AutomaticImportReconciledDownloads
                    ? "Recovered"
                    : "ImportPending");
                download.SetMetadata(ReasonKey, "Matched files were stable, readable, and associated with the tracked audiobook.");

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

        private List<string> EnumerateAudioFiles(string root)
        {
            var rootPath = Path.GetFullPath(root);
            var rootPrefix = Path.EndsInDirectorySeparator(rootPath)
                ? rootPath
                : rootPath + Path.DirectorySeparatorChar;
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var directories = new Stack<(string Path, int Depth)>();
            var files = new List<string>();
            directories.Push((rootPath, 0));

            while (directories.Count > 0 && files.Count < MaximumScannedFiles)
            {
                var (directory, depth) = directories.Pop();
                try
                {
                    foreach (var path in fileSystem.EnumerateFiles(directory))
                    {
                        if (files.Count >= MaximumScannedFiles)
                        {
                            break;
                        }

                        if (FileUtils.IsAudioFile(path)
                            && !fileSystem.IsReparsePoint(path)
                            && IsWithinRoot(path, rootPrefix, pathComparison))
                        {
                            files.Add(path);
                        }
                    }

                    if (depth >= MaximumDirectoryDepth)
                    {
                        continue;
                    }

                    foreach (var child in fileSystem.EnumerateDirectories(directory))
                    {
                        if (!fileSystem.IsReparsePoint(child)
                            && IsWithinRoot(child, rootPrefix, pathComparison))
                        {
                            directories.Push((child, depth + 1));
                        }
                    }
                }
                catch (UnauthorizedAccessException exception)
                {
                    logger.LogWarning(
                        exception,
                        "Unable to inspect configured completed-download directory {Directory}",
                        LogRedaction.SanitizeFilePath(directory));
                }
                catch (IOException exception)
                {
                    logger.LogWarning(
                        exception,
                        "Unable to inspect configured completed-download directory {Directory}",
                        LogRedaction.SanitizeFilePath(directory));
                }
            }

            return files;
        }

        private async Task<List<StableCandidateFile>> FindMatchingFilesAsync(
            IReadOnlyCollection<string> candidateFiles,
            IDictionary<string, AudioMetadata?> metadataByPath,
            Audiobook audiobook,
            CancellationToken cancellationToken)
        {
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var matchingGroups = new List<List<StableCandidateFile>>();
            var groups = candidateFiles.GroupBy(Path.GetDirectoryName, pathComparer);
            foreach (var group in groups)
            {
                var files = new List<(string Path, AudioMetadata? Metadata)>();
                foreach (var path in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanRead(path))
                    {
                        files.Clear();
                        break;
                    }

                    if (!metadataByPath.TryGetValue(path, out var metadata))
                    {
                        metadata = await metadataService.ExtractFileMetadataAsync(path);
                        metadataByPath[path] = metadata;
                    }

                    files.Add((path, metadata));
                }

                if (files.Count == 0)
                {
                    continue;
                }

                var matchingFiles = files
                    .Where(file => MatchesAudiobook(file, audiobook))
                    .ToList();
                if (matchingFiles.Count == 0
                    || (matchingFiles.Count > 1
                        && matchingFiles.Any(file => !string.Equals(
                            Path.GetExtension(file.Path),
                            ".mp3",
                            StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                try
                {
                    matchingGroups.Add(matchingFiles
                        .Select(file => new StableCandidateFile(
                            file.Path,
                            fileSystem.GetFileLength(file.Path),
                            fileSystem.GetLastWriteTimeUtc(file.Path)))
                        .ToList());
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
            }

            return matchingGroups.Count == 1 ? matchingGroups[0] : [];
        }

        private bool CanRead(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return stream.ReadByte() >= 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool MatchesAudiobook(
            (string Path, AudioMetadata? Metadata) file,
            Audiobook audiobook)
        {
            var title = Normalize(audiobook.Title);
            var authorNames = audiobook.Authors?
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Select(Normalize)
                .ToList() ?? [];
            if (string.IsNullOrWhiteSpace(title) || authorNames.Count == 0)
            {
                return false;
            }

            var metadata = file.Metadata;
            if (!string.IsNullOrWhiteSpace(metadata?.Asin)
                && !string.IsNullOrWhiteSpace(audiobook.Asin)
                && !string.Equals(metadata.Asin, audiobook.Asin, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fileAsin = FirstNonEmpty(metadata?.Asin, Path.GetFileNameWithoutExtension(file.Path));
            if (!string.IsNullOrWhiteSpace(audiobook.Asin)
                && ContainsIdentifier(fileAsin, audiobook.Asin))
            {
                return true;
            }

            var titleMatches = string.Equals(Normalize(metadata?.Album), title, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(metadata?.Title), title, StringComparison.OrdinalIgnoreCase);
            if (!titleMatches)
            {
                return false;
            }

            var artist = Normalize(FirstNonEmpty(metadata?.AlbumArtist, metadata?.Artist));
            return authorNames.Any(author =>
                string.Equals(artist, author, StringComparison.OrdinalIgnoreCase)
                || artist.Contains(author, StringComparison.OrdinalIgnoreCase));
        }

        private bool AreFilesStable(
            Download download,
            IReadOnlyCollection<StableCandidateFile> files,
            int minimumStabilitySeconds)
        {
            if (files.Any(file => file.Length <= 0))
            {
                SetAttention(download, "A recovered audio file is empty.");
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
                download.SetMetadata(StateKey, "StabilityChecking");
                return false;
            }

            return now - snapshot.FirstObservedUtc >= TimeSpan.FromSeconds(Math.Clamp(minimumStabilitySeconds, 15, 3600));
        }

        private static bool HasCompletionEvidence(Download download, IReadOnlyCollection<StableCandidateFile> files)
        {
            var clientState = download.GetMetadataString("ClientState")?.Trim().ToLowerInvariant();
            if (clientState is "paused" or "stoppeddl" or "stoppedup" or "downloading" or "queued" or "queueddl")
            {
                return false;
            }

            if (clientState is "completed" or "success" or "seeding" or "finished"
                || (download.TotalSize > 0 && download.DownloadedSize >= download.TotalSize))
            {
                return true;
            }

            return download.ExpectedFileSize is > 0
                && files.Sum(file => file.Length) >= download.ExpectedFileSize.Value;
        }

        private static bool CanRecover(Download download)
        {
            if (download.Status is not (DownloadStatus.Queued
                or DownloadStatus.Downloading
                or DownloadStatus.ImportPending))
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

        private static void SetAttention(Download download, string reason)
        {
            download.SetMetadata(StateKey, "NeedsAttention");
            download.SetMetadata(ReasonKey, reason);
        }

        private static bool IsWithinRoot(string path, string rootPrefix, StringComparison comparison)
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(rootPrefix, comparison);
        }

        private static bool ContainsIdentifier(string value, string identifier) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Contains(identifier, StringComparison.OrdinalIgnoreCase);

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        private static string Normalize(string? value) =>
            TitleUtils.NormalizeTitle(value ?? string.Empty).Trim();

        private sealed record StabilitySnapshot(string Signature, DateTime FirstObservedUtc);

        private sealed record StableCandidateFile(string Path, long Length, DateTime LastWriteTimeUtc);
    }
}
