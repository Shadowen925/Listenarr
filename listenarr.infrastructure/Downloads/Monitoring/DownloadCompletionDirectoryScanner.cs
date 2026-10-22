using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    internal sealed record DownloadCompletionDirectoryScanResult(
        List<string> Files,
        bool IsComplete);

    internal sealed partial class DownloadCompletionDirectoryScanner(
        IRemotePathMappingService remotePathMappingService,
        IFileSystem fileSystem,
        ILogger<DownloadCompletionReconciler> logger,
        int maximumScannedFiles = 1000,
        int maximumDirectoryDepth = 5,
        int maximumScannedEntries = 10000)
    {
        private readonly int _maximumScannedFiles = Math.Max(1, maximumScannedFiles);
        private readonly int _maximumDirectoryDepth = Math.Max(0, maximumDirectoryDepth);
        private readonly int _maximumScannedEntries = Math.Max(1, maximumScannedEntries);

        public async Task<(List<string> Roots, string FailureReason)> ResolveAuthorizedRootsAsync(
            DownloadClientConfiguration client,
            IReadOnlyCollection<RemotePathMapping> mappings,
            CancellationToken cancellationToken)
        {
            var roots = new List<string>();
            var failureReasons = new List<string>();
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            foreach (var mapping in mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(mapping.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string translatedRoot;
                try
                {
                    translatedRoot = await remotePathMappingService.TranslatePathAsync(client, mapping.RemotePath);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (InvalidOperationException exception)
                {
                    failureReasons.Add("A configured remote path mapping could not be translated.");
                    logger.LogWarning(
                        exception,
                        "Skipping completed-download mapping {MappingId} for client {ClientId}: mapping cannot be translated",
                        mapping.Id,
                        client.Id);
                    continue;
                }

                if (!TryAuthorizeMappedRoot(mapping.LocalPath, translatedRoot, out var root, out var reason))
                {
                    failureReasons.Add(reason);
                    logger.LogWarning(
                        "Skipping completed-download mapping {MappingId} for client {ClientId}: {Reason}",
                        mapping.Id,
                        client.Id,
                        reason);
                    continue;
                }

                if (!roots.Contains(root, pathComparer))
                {
                    roots.Add(root);
                }
            }

            roots.Sort(pathComparer);
            var failureReason = failureReasons.Count == 0
                ? "No configured client mapping resolves to an available, authorized local completed-download directory."
                : string.Join(" ", failureReasons.Distinct(StringComparer.Ordinal));
            return (roots, failureReason);
        }

        public async Task<(string? Directory, bool MappingUnavailable)> ResolveDownloadSpecificDirectoryAsync(
            DownloadClientConfiguration client,
            IReadOnlyCollection<string> authorizedRoots,
            IEnumerable<string?> persistedPaths,
            CancellationToken cancellationToken)
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var candidates = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

            foreach (var persistedPath in persistedPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string translatedPath;
                try
                {
                    translatedPath = await remotePathMappingService.TranslatePathAsync(client, persistedPath!);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogDebug(
                        exception,
                        "Unable to verify persisted completed-download path for client {ClientId}: its owning path mapping is unavailable",
                        client.Id);
                    return (null, true);
                }

                if (string.IsNullOrWhiteSpace(translatedPath) || !Path.IsPathFullyQualified(translatedPath))
                {
                    continue;
                }

                if (persistedPath!.StartsWith("//", StringComparison.Ordinal)
                    && translatedPath.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var fullPath = Path.GetFullPath(translatedPath);
                    var isFile = fileSystem.FileExists(fullPath);
                    var directory = fileSystem.DirectoryExists(fullPath)
                        ? fullPath
                        : isFile
                            ? Path.GetDirectoryName(fullPath)
                            : null;
                    if (string.IsNullOrWhiteSpace(directory) || !fileSystem.DirectoryExists(directory))
                    {
                        continue;
                    }

                    var authorizedRoot = authorizedRoots.FirstOrDefault(root =>
                        IsWithinOrEqual(directory, root, comparison));
                    if (authorizedRoot == null
                        || !HasNoReparsePointBetween(directory, authorizedRoot, comparison)
                        || string.Equals(
                            Path.TrimEndingDirectorySeparator(directory),
                            Path.TrimEndingDirectorySeparator(authorizedRoot),
                            comparison))
                    {
                        continue;
                    }

                    candidates.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    logger.LogDebug(
                        exception,
                        "Ignoring invalid or inaccessible persisted completed-download path for client {ClientId}",
                        client.Id);
                }
            }

            if (candidates.Count == 1)
            {
                var candidate = candidates.Single();
                logger.LogDebug(
                    "Using verified download-specific directory {Directory} for client {ClientId}",
                    LogRedaction.SanitizeFilePath(candidate),
                    client.Id);
                return (candidate, false);
            }

            if (candidates.Count > 1)
            {
                logger.LogWarning(
                    "Ignoring conflicting persisted completed-download directories for client {ClientId}; broad recovery discovery will remain ambiguity-safe",
                    client.Id);
            }

            return (null, false);
        }

        public DownloadCompletionDirectoryScanResult EnumerateAudioFiles(
            IReadOnlyCollection<string> roots,
            CancellationToken cancellationToken)
        {
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var files = new List<string>();
            var isComplete = true;
            var fileLimitReached = false;
            var depthLimitReached = false;
            var entryLimitReached = false;
            var scannedEntries = 0;

            foreach (var root in roots.OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rootPrefix = Path.EndsInDirectorySeparator(root)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                try
                {
                    if (!fileSystem.DirectoryExists(root) || fileSystem.IsReparsePoint(root))
                    {
                        isComplete = false;
                        logger.LogWarning(
                            "Unable to inspect authorized completed-download root {Directory}: it is missing or became a link",
                            LogRedaction.SanitizeFilePath(root));
                        continue;
                    }
                }
                catch (UnauthorizedAccessException exception)
                {
                    isComplete = false;
                    logger.LogWarning(
                        exception,
                        "Unable to inspect authorized completed-download root {Directory}",
                        LogRedaction.SanitizeFilePath(root));
                    continue;
                }
                catch (IOException exception)
                {
                    isComplete = false;
                    logger.LogWarning(
                        exception,
                        "Unable to inspect authorized completed-download root {Directory}",
                        LogRedaction.SanitizeFilePath(root));
                    continue;
                }

                var directories = new Stack<(string Path, int Depth)>();
                directories.Push((root, 0));

                while (directories.Count > 0 && !fileLimitReached)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (directory, depth) = directories.Pop();
                    try
                    {
                        var filePaths = EnumerateSortedPaths(
                            fileSystem.EnumerateFiles(directory),
                            cancellationToken,
                            ref scannedEntries,
                            out entryLimitReached);
                        if (entryLimitReached)
                        {
                            isComplete = false;
                            break;
                        }

                        foreach (var path in filePaths)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (FileUtils.IsAudioFile(path)
                                && !fileSystem.IsReparsePoint(path)
                                && IsWithinRoot(path, rootPrefix, pathComparison))
                            {
                                if (files.Count >= _maximumScannedFiles)
                                {
                                    fileLimitReached = true;
                                    isComplete = false;
                                    break;
                                }

                                files.Add(Path.GetFullPath(path));
                            }
                        }

                        if (fileLimitReached)
                        {
                            break;
                        }

                        var children = EnumerateSortedPaths(
                                fileSystem.EnumerateDirectories(directory),
                                cancellationToken,
                                ref scannedEntries,
                                out entryLimitReached)
                            .Where(path => !fileSystem.IsReparsePoint(path)
                                && IsWithinRoot(path, rootPrefix, pathComparison))
                            .ToList();
                        if (entryLimitReached)
                        {
                            isComplete = false;
                            break;
                        }

                        if (depth >= _maximumDirectoryDepth)
                        {
                            if (children.Count > 0)
                            {
                                depthLimitReached = true;
                                isComplete = false;
                            }

                            continue;
                        }

                        for (var index = children.Count - 1; index >= 0; index--)
                        {
                            directories.Push((children[index], depth + 1));
                        }
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        isComplete = false;
                        logger.LogWarning(
                            exception,
                            "Unable to inspect authorized completed-download directory {Directory}; continuing with accessible directories",
                            LogRedaction.SanitizeFilePath(directory));
                    }
                    catch (IOException exception)
                    {
                        isComplete = false;
                        logger.LogWarning(
                            exception,
                            "Unable to inspect authorized completed-download directory {Directory}; continuing with accessible directories",
                            LogRedaction.SanitizeFilePath(directory));
                    }
                }

                if (fileLimitReached)
                {
                    break;
                }

                if (entryLimitReached)
                {
                    break;
                }
            }

            if (fileLimitReached)
            {
                logger.LogWarning(
                    "Completed-download scan reached its file limit of {MaximumScannedFiles}; recovered-file discovery is incomplete",
                    _maximumScannedFiles);
            }

            if (depthLimitReached)
            {
                logger.LogWarning(
                    "Completed-download scan reached its directory-depth limit of {MaximumDirectoryDepth}; deeper directories were not scanned",
                    _maximumDirectoryDepth);
            }

            if (entryLimitReached)
            {
                logger.LogWarning(
                    "Completed-download scan reached its directory-entry limit of {MaximumScannedEntries}; recovered-file discovery is incomplete",
                    _maximumScannedEntries);
            }

            return new DownloadCompletionDirectoryScanResult(
                files.Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList(),
                isComplete);
        }

        private List<string> EnumerateSortedPaths(
            IEnumerable<string> paths,
            CancellationToken cancellationToken,
            ref int scannedEntries,
            out bool limitReached)
        {
            var results = new List<string>();
            limitReached = false;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scannedEntries >= _maximumScannedEntries)
                {
                    limitReached = true;
                    break;
                }

                results.Add(path);
                scannedEntries++;
            }

            results.Sort(StringComparer.Ordinal);
            return results;
        }
    }
}
