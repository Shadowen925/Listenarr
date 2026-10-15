namespace Listenarr.Application.Audiobookshelf;

public static class AudiobookshelfPathMapper
{
    public static bool TryMapPath(
        string audiobookshelfPath,
        IEnumerable<string> audiobookshelfRoots,
        string listenarrRoot,
        out string mappedPath)
    {
        mappedPath = string.Empty;
        if (!TryNormalizeRemotePath(audiobookshelfPath, out var normalizedPath))
        {
            return false;
        }

        var matchingRoot = audiobookshelfRoots
            .Select(root => TryNormalizeRemotePath(root, out var normalizedRoot)
                ? normalizedRoot
                : null)
            .Where(root => root != null
                && (string.Equals(normalizedPath, root, StringComparison.Ordinal)
                    || (root == "/"
                        ? normalizedPath.StartsWith("/", StringComparison.Ordinal)
                        : normalizedPath.StartsWith(root + "/", StringComparison.Ordinal))))
            .OrderByDescending(root => root!.Length)
            .FirstOrDefault();
        if (matchingRoot == null)
        {
            return false;
        }

        var relativePath = normalizedPath[matchingRoot.Length..].TrimStart('/');
        if (relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
        {
            return false;
        }

        try
        {
            var normalizedListenarrRoot = Path.GetFullPath(listenarrRoot);
            var candidate = relativePath.Length == 0
                ? normalizedListenarrRoot
                : Path.GetFullPath(Path.Combine(
                    normalizedListenarrRoot,
                    Path.Combine(relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))));
            var relativeToRoot = Path.GetRelativePath(normalizedListenarrRoot, candidate);
            if (Path.IsPathRooted(relativeToRoot)
                || relativeToRoot == ".."
                || relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relativeToRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return false;
            }

            mappedPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryNormalizeRemotePath(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().Replace('\\', '/');
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        normalized = "/" + string.Join('/', segments);
        return true;
    }
}
