using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Downloads.Processing;

public partial class DownloadProcessingJobProcessor
{
    private async Task<ImportedAudioOutputProofResult> CaptureImportedAudioOutputsAsync(
        IServiceProvider services,
        Audiobook audiobook,
        IReadOnlyCollection<ImportResult> results,
        CancellationToken cancellationToken)
    {
        var fileRepository = services.GetRequiredService<IAudiobookFileRepository>();
        var registeredFiles = await fileRepository.GetByAudiobookIdAsync(
            audiobook.Id,
            cancellationToken);
        var registeredAudioFiles = registeredFiles
            .Where(file => FileUtils.IsAudioFile(file.Path ?? string.Empty))
            .ToList();
        if (registeredAudioFiles.Count == 0)
        {
            return ImportedAudioOutputProofResult.Failure(
                "No audio files were registered after file import");
        }

        var audioResults = results
            .Where(result =>
                FileUtils.IsAudioFile(result.SourcePath ?? string.Empty)
                || FileUtils.IsAudioFile(result.FinalPath ?? string.Empty))
            .ToList();
        var finalPaths = new List<string>();
        var skippedAudioResults = 0;
        foreach (var result in audioResults)
        {
            if (!result.Success)
            {
                return ImportedAudioOutputProofResult.Failure(
                    "An audio import result was unsuccessful");
            }

            if (!string.IsNullOrWhiteSpace(result.FinalPath))
            {
                if (!FileUtils.IsAudioFile(result.FinalPath))
                {
                    return ImportedAudioOutputProofResult.Failure(
                        $"Audio source {result.SourcePath} has a non-audio final path");
                }

                finalPaths.Add(result.FinalPath);
            }
            else if (FileUtils.IsAudioFile(result.SourcePath ?? string.Empty))
            {
                skippedAudioResults++;
            }
        }

        if (skippedAudioResults > 0 && finalPaths.Count > 0)
        {
            return ImportedAudioOutputProofResult.Failure(
                "The import produced only some of its audio outputs; skipped audio cannot be reconciled safely");
        }

        if (finalPaths.Count == 0)
        {
            var pathIdentityResolver =
                services.GetRequiredService<IAudiobookFilePathIdentityResolver>();
            foreach (var file in registeredAudioFiles)
            {
                if (string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }

                var identity = await pathIdentityResolver.ResolveAsync(
                    audiobook,
                    file.Path,
                    cancellationToken);
                if (identity.State == PathIdentityState.Valid)
                {
                    finalPaths.Add(identity.CanonicalPath);
                }
            }
        }

        if (finalPaths.Count == 0)
        {
            return ImportedAudioOutputProofResult.Failure(
                "No registered audio output could be resolved for the import");
        }

        if (string.IsNullOrWhiteSpace(audiobook.BasePath))
        {
            return ImportedAudioOutputProofResult.Failure(
                "The audiobook has no library base path for import verification");
        }

        var semanticsResolution = await services
            .GetRequiredService<IFileSystemSemanticsResolver>()
            .ResolveAsync(
                audiobook.BasePath,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
        if (semanticsResolution.State != PathIdentityState.Valid)
        {
            return ImportedAudioOutputProofResult.Failure(
                $"The audiobook library path cannot be verified: {semanticsResolution.Reason ?? "filesystem identity is unavailable"}");
        }

        var semantics = semanticsResolution.Semantics;
        var canonicalRoot = FileSystemPathIdentity.Canonicalize(
            audiobook.BasePath,
            semantics.Syntax);
        var seenPaths = new HashSet<string>(semantics.Comparer);
        var outputs = new List<ImportedAudioOutput>(finalPaths.Count);
        foreach (var path in finalPaths)
        {
            string absolutePath;
            try
            {
                absolutePath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or IOException)
            {
                return ImportedAudioOutputProofResult.Failure(
                    $"An imported audio output path is invalid: {exception.Message}");
            }

            var canonicalPath = FileSystemPathIdentity.Canonicalize(
                absolutePath,
                semantics.Syntax);
            if (!FileSystemPathIdentity.IsSameOrInside(
                    canonicalPath,
                    canonicalRoot,
                    semantics))
            {
                return ImportedAudioOutputProofResult.Failure(
                    $"Imported audio output {absolutePath} is outside the audiobook library path");
            }

            if (!seenPaths.Add(canonicalPath))
            {
                return ImportedAudioOutputProofResult.Failure(
                    "The import produced duplicate final audio output paths");
            }

            try
            {
                var length = new FileInfo(absolutePath).Length;
                if (length <= 0)
                {
                    return ImportedAudioOutputProofResult.Failure(
                        $"Imported audio output is empty: {absolutePath}");
                }

                outputs.Add(new ImportedAudioOutput(absolutePath, length));
            }
            catch (IOException exception)
            {
                return ImportedAudioOutputProofResult.Failure(
                    $"Unable to inspect imported audio output {absolutePath}: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                return ImportedAudioOutputProofResult.Failure(
                    $"Unable to read imported audio output {absolutePath}: {exception.Message}");
            }
        }

        return new ImportedAudioOutputProofResult(outputs, null);
    }

    private sealed record ImportedAudioOutput(string Path, long Length);

    private sealed record ImportedAudioOutputProofResult(
        IReadOnlyList<ImportedAudioOutput> Outputs,
        string? Error)
    {
        public static ImportedAudioOutputProofResult Failure(string error) =>
            new([], error);
    }
}
