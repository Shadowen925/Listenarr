using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    internal sealed class DownloadCompletionDirectoryScanner(
        IRemotePathMappingService remotePathMappingService,
        IFileSystem fileSystem,
        ILogger<DownloadCompletionReconciler> logger)
    {
        private const int MaximumScannedFiles = 1000;
        private const int MaximumDirectoryDepth = 5;

        public async Task<(List<string> Roots, string FailureReason)> ResolveAuthorizedRootsAsync(
            DownloadClientConfiguration client,
            IReadOnlyCollection<RemotePathMapping> mappings)
        {
            var roots = new List<string>();
            var failureReasons = new List<string>();
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            foreach (var mapping in mappings)
            {
                if (!string.Equals(mapping.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string translatedRoot;
                try
                {
                    translatedRoot = await remotePathMappingService.TranslatePathAsync(client, mapping.RemotePath);
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

            var failureReason = failureReasons.Count == 0
                ? "No configured client mapping resolves to an available, authorized local completed-download directory."
                : string.Join(" ", failureReasons.Distinct(StringComparer.Ordinal));
            return (roots, failureReason);
        }

        public List<string> EnumerateAudioFiles(IReadOnlyCollection<string> roots)
        {
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var files = new List<string>();
            foreach (var root in roots)
            {
                var rootPrefix = Path.EndsInDirectorySeparator(root)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                var directories = new Stack<(string Path, int Depth)>();
                directories.Push((root, 0));

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
            }

            return files
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                .ToList();
        }

        private bool TryAuthorizeMappedRoot(
            string configuredLocalRoot,
            string translatedRoot,
            out string root,
            out string reason)
        {
            root = string.Empty;
            reason = "The mapping did not resolve to a valid local directory.";
            if (string.IsNullOrWhiteSpace(configuredLocalRoot)
                || string.IsNullOrWhiteSpace(translatedRoot)
                || !Path.IsPathFullyQualified(configuredLocalRoot)
                || !Path.IsPathFullyQualified(translatedRoot))
            {
                reason = "The mapping local path must be an absolute path in Listenarr's filesystem namespace.";
                return false;
            }

            try
            {
                var configuredFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredLocalRoot));
                var translatedFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(translatedRoot));
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(configuredFullPath, translatedFullPath, comparison))
                {
                    reason = "The remote path did not resolve to the local root authorized by this mapping.";
                    return false;
                }

                if (!fileSystem.DirectoryExists(translatedFullPath))
                {
                    reason = "The mapped local directory is missing or is not mounted in Listenarr.";
                    return false;
                }

                var pathRoot = Path.GetPathRoot(translatedFullPath);
                for (var current = translatedFullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)!)
                {
                    if (fileSystem.IsReparsePoint(current))
                    {
                        reason = "The mapped directory contains a symbolic link or reparse point.";
                        return false;
                    }

                    if (string.Equals(
                        Path.TrimEndingDirectorySeparator(current),
                        Path.TrimEndingDirectorySeparator(pathRoot ?? string.Empty),
                        comparison))
                    {
                        break;
                    }
                }

                root = translatedFullPath;
                reason = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                reason = $"The mapped local directory is invalid or inaccessible: {exception.Message}";
                return false;
            }
        }

        private static bool IsWithinRoot(string path, string rootPrefix, StringComparison comparison)
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(rootPrefix, comparison);
        }
    }
}
