namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    internal sealed partial class DownloadCompletionDirectoryScanner
    {
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
            catch (Exception exception) when (
                exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                reason = $"The mapped local directory is invalid or inaccessible: {exception.Message}";
                return false;
            }
        }

        private bool HasNoReparsePointBetween(
            string directory,
            string authorizedRoot,
            StringComparison comparison)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(authorizedRoot));
            for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                !string.IsNullOrEmpty(current) && IsWithinOrEqual(current, root, comparison);
                current = Path.GetDirectoryName(current)!)
            {
                if (fileSystem.IsReparsePoint(current))
                {
                    return false;
                }

                if (string.Equals(current, root, comparison))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsWithinOrEqual(string path, string root, StringComparison comparison)
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (string.Equals(fullPath, fullRoot, comparison))
            {
                return true;
            }

            var relativePath = Path.GetRelativePath(fullRoot, fullPath);
            return !Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", comparison)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", comparison);
        }

        private static bool IsWithinRoot(string path, string rootPrefix, StringComparison comparison)
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(rootPrefix, comparison);
        }
    }
}
