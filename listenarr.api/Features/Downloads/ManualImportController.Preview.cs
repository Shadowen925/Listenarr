using Microsoft.AspNetCore.Mvc;
using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    /// <summary>
    /// Preview audio files and, when a library match is supplied, their planned destinations.
    /// </summary>
    /// <param name="path">Absolute path to the import source.</param>
    /// <param name="audiobookId">Optional library match for destination and naming preview.</param>
    /// <param name="cancellationToken">Cancels filesystem and metadata inspection.</param>
    /// <returns>Audio files with relative paths, sizes, and optional planned destinations.</returns>
    [HttpGet("preview")]
    public async Task<ActionResult<object>> Preview(
        [FromQuery] string path,
        [FromQuery] int? audiobookId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest(new { error = "Path is required" });
            }

            var normalized = Path.GetFullPath(path);
            var settings = await _configService.GetApplicationSettingsAsync();
            var isSingleFile = _fileSystem.FileExists(normalized);
            var sourceDirectory = isSingleFile ? Path.GetDirectoryName(normalized) : normalized;
            if (string.IsNullOrWhiteSpace(sourceDirectory)
                || (!isSingleFile && !_fileSystem.DirectoryExists(normalized)))
            {
                return NotFound(new { error = "Import source not found" });
            }

            var files = (isSingleFile
                    ? new[] { normalized }
                    : _fileSystem.EnumerateFiles(normalized, "*.*", SearchOption.AllDirectories))
                .Where(file => !FileUtils.IsBlacklistedFile(file, settings.ImportBlacklistExtensions))
                .Where(FileUtils.IsAudioFile)
                .Select(file => new
                {
                    relativePath = Path.GetRelativePath(sourceDirectory, file),
                    fullPath = file,
                    size = _fileSystem.GetFileLength(file),
                    series = (string?)null,
                    season = (string?)null,
                    episodes = (string?)null,
                    quality = (string?)null,
                    languages = new[] { "English" },
                    releaseType = "Unknown"
                })
                .ToList();

            if (audiobookId is <= 0)
            {
                return BadRequest(new { error = "Audiobook ID must be positive." });
            }

            Audiobook? audiobook = null;
            IReadOnlyCollection<RootFolder> rootFolders = [];
            IReadOnlyList<string> allowedRoots = [];
            string? managedBasePath = null;
            FileSystemPathSemantics? destinationSemantics = null;
            if (audiobookId.HasValue)
            {
                audiobook = await _audiobookRepository.GetByIdAsync(audiobookId.Value);
                if (audiobook is null)
                {
                    return NotFound(new { error = "Audiobook not found." });
                }

                rootFolders = await _rootFolderService.GetAllAsync();
                if (TryResolveManagedDestinationBasePath(
                        audiobook,
                        rootFolders,
                        settings,
                        out var resolvedBasePath,
                        out allowedRoots,
                        out _))
                {
                    managedBasePath = resolvedBasePath;
                    destinationSemantics = (await ResolveDestinationResolutionAsync(
                        managedBasePath,
                        rootFolders,
                        cancellationToken)).Semantics;
                }
            }

            var items = new List<object>(files.Count);
            foreach (var file in files)
            {
                string? destinationPath = null;
                if (audiobook is not null
                    && managedBasePath is not null
                    && destinationSemantics.HasValue)
                {
                    var metadata = await _metadataService.ExtractFileMetadataAsync(file.fullPath);
                    if (metadata is not null)
                    {
                        var plan = await _pathPlanner.GeneratePathAsync(
                            audiobook,
                            metadata,
                            new ManualImportItemDto
                            {
                                FullPath = file.fullPath,
                                RelativePath = file.relativePath,
                                MatchedAudiobookId = audiobook.Id
                            },
                            managedBasePath,
                            rootFolders.ToList(),
                            settings,
                            destinationSemantics.Value,
                            files.Count > 1);
                        if (_fileSystem.TryValidateMutationTarget(
                                plan.DestinationPath,
                                allowedRoots,
                                out var validatedDestination,
                                out _))
                        {
                            destinationPath = validatedDestination;
                        }
                    }
                }

                items.Add(new
                {
                    file.relativePath,
                    file.fullPath,
                    size = FormatSize(file.size),
                    file.series,
                    file.season,
                    file.episodes,
                    file.quality,
                    file.languages,
                    file.releaseType,
                    destinationPath
                });
            }

            return Ok(new { items });
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            _logger.LogError(exception, "Error previewing manual import for path {Path}", path);
            return StatusCode(500, new { error = "Failed to preview import" });
        }
    }
}
