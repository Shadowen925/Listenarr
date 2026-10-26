using Listenarr.Domain.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    internal sealed partial class DownloadCompletionReconciler
    {
        private static readonly TimeSpan CandidateMetadataCacheDuration = TimeSpan.FromMinutes(15);

        private async Task<MatchingFileSearchResult> FindMatchingFilesAsync(
            IReadOnlyCollection<string> candidateFiles,
            IDictionary<string, AudioMetadata?> metadataByPath,
            Audiobook audiobook,
            CancellationToken cancellationToken)
        {
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var matchingGroups = new List<List<StableCandidateFile>>();
            var isComplete = true;
            var groups = candidateFiles.GroupBy(Path.GetDirectoryName, pathComparer);
            foreach (var group in groups)
            {
                var files = new List<(string Path, AudioMetadata? Metadata)>();
                foreach (var path in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanRead(path))
                    {
                        isComplete = false;
                        logger.LogWarning(
                            "Unable to read completed-download candidate {Path}; recovery discovery will remain incomplete",
                            LogRedaction.SanitizeFilePath(path));
                        files.Clear();
                        break;
                    }

                    if (!metadataByPath.TryGetValue(path, out var metadata))
                    {
                        try
                        {
                            metadata = await GetCandidateMetadataAsync(path, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            metadataByPath[path] = metadata;
                        }
                        catch (UnauthorizedAccessException exception)
                        {
                            isComplete = false;
                            logger.LogWarning(
                                exception,
                                "Unable to read metadata from completed-download candidate {Path}",
                                LogRedaction.SanitizeFilePath(path));
                            files.Clear();
                            break;
                        }
                        catch (IOException exception)
                        {
                            isComplete = false;
                            logger.LogWarning(
                                exception,
                                "Unable to read metadata from completed-download candidate {Path}",
                                LogRedaction.SanitizeFilePath(path));
                            files.Clear();
                            break;
                        }
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
                if (matchingFiles.Count == 0)
                {
                    continue;
                }

                var verifiedMatchingFiles = new List<(string Path, AudioMetadata? Metadata)>();
                foreach (var file in matchingFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AudioMetadata? verifiedMetadata;
                    try
                    {
                        verifiedMetadata = await metadataService.ExtractFileMetadataAsync(file.Path);
                        cancellationToken.ThrowIfCancellationRequested();
                        CacheCandidateMetadata(file.Path, verifiedMetadata);
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        isComplete = false;
                        logger.LogWarning(
                            exception,
                            "Unable to verify metadata from completed-download candidate {Path}",
                            LogRedaction.SanitizeFilePath(file.Path));
                        files.Clear();
                        break;
                    }
                    catch (IOException exception)
                    {
                        isComplete = false;
                        logger.LogWarning(
                            exception,
                            "Unable to verify metadata from completed-download candidate {Path}",
                            LogRedaction.SanitizeFilePath(file.Path));
                        files.Clear();
                        break;
                    }

                    var verifiedFile = (file.Path, verifiedMetadata);
                    metadataByPath[file.Path] = verifiedMetadata;
                    if (MatchesAudiobook(verifiedFile, audiobook))
                    {
                        verifiedMatchingFiles.Add(verifiedFile);
                    }
                }

                if (verifiedMatchingFiles.Count == 0)
                {
                    continue;
                }

                try
                {
                    matchingGroups.Add(verifiedMatchingFiles
                        .Select(file => new StableCandidateFile(
                            file.Path,
                            fileSystem.GetFileLength(file.Path),
                            fileSystem.GetLastWriteTimeUtc(file.Path),
                            file.Metadata))
                        .ToList());
                }
                catch (UnauthorizedAccessException exception)
                {
                    isComplete = false;
                    logger.LogWarning(
                        exception,
                        "Unable to inspect matching completed-download candidate files");
                    continue;
                }
                catch (IOException exception)
                {
                    isComplete = false;
                    logger.LogWarning(
                        exception,
                        "Unable to inspect matching completed-download candidate files");
                    continue;
                }
            }

            return new MatchingFileSearchResult(
                matchingGroups.Count == 1 ? matchingGroups[0] : null,
                matchingGroups.Count,
                isComplete);
        }

        private async Task<AudioMetadata?> GetCandidateMetadataAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var cacheKey = CreateCandidateMetadataCacheKey(path);
            if (metadataCache.TryGetValue(cacheKey, out CachedCandidateMetadata? cached))
            {
                return cached?.Metadata;
            }

            var metadata = await metadataService.ExtractFileMetadataAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            CacheCandidateMetadata(cacheKey, metadata);
            return metadata;
        }

        private void CacheCandidateMetadata(string path, AudioMetadata? metadata)
        {
            if (metadata != null)
            {
                CacheCandidateMetadata(CreateCandidateMetadataCacheKey(path), metadata);
            }
        }

        private void CacheCandidateMetadata(
            CandidateMetadataCacheKey cacheKey,
            AudioMetadata? metadata)
        {
            if (metadata != null)
            {
                metadataCache.Set(
                    cacheKey,
                    new CachedCandidateMetadata(metadata),
                    CandidateMetadataCacheDuration);
            }
        }

        private CandidateMetadataCacheKey CreateCandidateMetadataCacheKey(string path) =>
            new(
                Path.GetFullPath(path),
                fileSystem.GetFileLength(path),
                fileSystem.GetLastWriteTimeUtc(path).Ticks);

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

        private static bool ContainsIdentifier(string value, string identifier) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Contains(identifier, StringComparison.OrdinalIgnoreCase);

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        private static string Normalize(string? value) =>
            TitleUtils.NormalizeTitle(value ?? string.Empty).Trim();

        private readonly record struct CandidateMetadataCacheKey(
            string Path,
            long Length,
            long LastWriteUtcTicks);

        private sealed record CachedCandidateMetadata(AudioMetadata Metadata);
    }
}
