using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Common;

internal static partial class ImportFinalizationFailureDiagnostics
{
    private const string OutputProofKey = "ImportedAudioOutputs";

    public static Task<string> DescribeIfFailedAsync(
        bool assertionFailed,
        IServiceProvider services,
        IDownloadRepository downloadRepository,
        IDownloadProcessingJobRepository jobRepository,
        string downloadId,
        string jobId,
        int? audiobookId) =>
        assertionFailed
            ? DescribeAsync(
                services,
                downloadRepository,
                jobRepository,
                downloadId,
                jobId,
                audiobookId)
            : Task.FromResult(string.Empty);

    public static async Task<string> DescribeAsync(
        IServiceProvider services,
        IDownloadRepository downloadRepository,
        IDownloadProcessingJobRepository jobRepository,
        string downloadId,
        string jobId,
        int? audiobookId)
    {
        var download = await downloadRepository.GetByIdAsync(downloadId);
        var job = await jobRepository.GetByIdAsync(jobId);
        var audiobook = audiobookId.HasValue
            ? await services.GetRequiredService<IAudiobookRepository>()
                .GetByIdAsync(audiobookId.Value)
            : null;
        var outputProofs = ReadOutputProofs(job);
        var outputPaths = outputProofs
            .Select(proof => proof.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var message = new StringBuilder();

        message.AppendLine("Import finalization failure diagnostics:");
        message.AppendLine($"DownloadId: {Sanitize(downloadId)}");
        message.AppendLine($"DownloadStatus: {download?.Status.ToString() ?? "not found"}");
        message.AppendLine($"DownloadPath: {Sanitize(download?.DownloadPath)}");
        message.AppendLine($"ProcessingJobId: {Sanitize(jobId)}");
        message.AppendLine($"ProcessingJobStatus: {job?.Status.ToString() ?? "not found"}");
        message.AppendLine($"RetryCount: {job?.RetryCount.ToString() ?? "not available"}");
        message.AppendLine($"ErrorMessage: {Sanitize(job?.ErrorMessage)}");
        message.AppendLine($"AudiobookId: {audiobookId?.ToString() ?? "not available"}");
        message.AppendLine($"AudiobookBasePath: {Sanitize(audiobook?.BasePath)}");
        message.AppendLine(
            $"Checkpoints: {FormatCheckpoints(job)}");
        message.AppendLine(
            $"ProcessingLog: {FormatProcessingLog(job)}");
        message.AppendLine(
            $"PersistedJobData: {FormatJobData(job)}");
        message.AppendLine(
            $"OutputProofs: {FormatOutputProofs(outputProofs)}");

        if (audiobookId.HasValue)
        {
            await AppendFileRegistrationDiagnosticsAsync(
                message,
                services,
                audiobook,
                outputPaths);
            await AppendOutstandingJournalDiagnosticsAsync(
                message,
                services,
                audiobookId.Value,
                outputPaths);
        }
        else
        {
            message.AppendLine("FileRegistration: audiobook ID unavailable");
            message.AppendLine("OutstandingPublicationJournals: audiobook ID unavailable");
        }

        return message.ToString();
    }

    private static async Task AppendFileRegistrationDiagnosticsAsync(
        StringBuilder message,
        IServiceProvider services,
        Audiobook? audiobook,
        IReadOnlyCollection<string> outputPaths)
    {
        if (audiobook == null)
        {
            message.AppendLine("FileRegistration: audiobook not found");
            return;
        }

        var fileRepository = services.GetRequiredService<IAudiobookFileRepository>();
        var files = await fileRepository.GetByAudiobookIdAsync(audiobook.Id);
        message.AppendLine(
            $"RegisteredAudioFiles: {FormatRegisteredFiles(files)}");

        var fileService = services.GetRequiredService<IAudiobookFileService>();
        foreach (var outputPath in outputPaths)
        {
            try
            {
                var ownership = await fileService.CheckAudiobookFileOwnershipAsync(
                    audiobook,
                    outputPath,
                    Path.GetDirectoryName(outputPath));
                message.AppendLine(
                    $"OwnershipVerification[{Sanitize(outputPath)}]: {ownership.Outcome}; " +
                    $"ExistingFileId={ownership.ExistingFile?.Id.ToString() ?? "none"}; " +
                    $"Reason={Sanitize(ownership.Reason)}");
            }
            catch (Exception exception) when (
                exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                message.AppendLine(
                    $"OwnershipVerification[{Sanitize(outputPath)}]: threw " +
                    $"{exception.GetType().Name}: {Sanitize(exception.Message)}");
            }
        }
    }

    private static async Task AppendOutstandingJournalDiagnosticsAsync(
        StringBuilder message,
        IServiceProvider services,
        int audiobookId,
        IReadOnlyCollection<string> outputPaths)
    {
        var contextFactory = services.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        var fileMutations = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.State != FileMutationJournalState.Completed
                && (journal.AudiobookId == audiobookId
                    || outputPaths.Contains(journal.DestinationPath)))
            .Select(journal => new
            {
                journal.OperationId,
                journal.State,
                journal.DestinationPath,
                journal.Error
            })
            .ToListAsync();
        var compatibilityPublications = await db.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.State != CompatibilityFilePublicationState.Completed
                && (journal.AudiobookId == audiobookId
                    || outputPaths.Contains(journal.DestinationPath)))
            .Select(journal => new
            {
                journal.OperationId,
                journal.State,
                journal.DestinationPath,
                journal.Error
            })
            .ToListAsync();

        message.AppendLine(
            $"OutstandingFileMutationJournals: {FormatJournals(fileMutations)}");
        message.AppendLine(
            $"OutstandingCompatibilityPublicationJournals: {FormatJournals(compatibilityPublications)}");
    }

    private static IReadOnlyList<OutputProof> ReadOutputProofs(DownloadProcessingJob? job)
    {
        if (job == null
            || !job.TryGetJobDataString(OutputProofKey, out var serializedProofs))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<OutputProof>>(serializedProofs) ?? [];
        }
        catch (JsonException)
        {
            return [new OutputProof($"<invalid proof JSON: {Sanitize(serializedProofs)}>", 0)];
        }
    }

    private static string FormatCheckpoints(DownloadProcessingJob? job)
    {
        if (job == null)
        {
            return "job unavailable";
        }

        string[] checkpointNames =
        [
            "FilesImported",
            "ClientMarkedImported",
            "ScanEnqueued",
            "AudiobookshelfScanRequested",
            "ImportCommitted"
        ];
        return string.Join(
            ", ",
            checkpointNames.Select(name => $"{name}={job.HasCheckpoint(name)}"));
    }

    private static string FormatProcessingLog(DownloadProcessingJob? job) =>
        job == null
            ? "job unavailable"
            : JsonSerializer.Serialize(job.ProcessingLog.Select(Sanitize));

    private static string FormatJobData(DownloadProcessingJob? job)
    {
        if (job == null)
        {
            return "job unavailable";
        }

        var safeData = job.JobData
            .Where(pair => !ContainsSensitiveKey(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => Sanitize(pair.Value is JsonElement element
                    ? element.ToString()
                    : pair.Value?.ToString()));
        return JsonSerializer.Serialize(safeData);
    }

    private static string FormatOutputProofs(IReadOnlyList<OutputProof> proofs) =>
        proofs.Count == 0
            ? "none"
            : JsonSerializer.Serialize(proofs.Select(proof => new
            {
                Path = Sanitize(proof.Path),
                proof.Length,
                Exists = !proof.Path.StartsWith("<invalid proof", StringComparison.Ordinal)
                    && File.Exists(proof.Path),
                ActualLength = !proof.Path.StartsWith("<invalid proof", StringComparison.Ordinal)
                    && File.Exists(proof.Path)
                        ? new FileInfo(proof.Path).Length
                        : (long?)null
            }));

    private static string FormatRegisteredFiles(IReadOnlyList<AudiobookFile> files) =>
        files.Count == 0
            ? "none"
            : JsonSerializer.Serialize(files.Select(file => new
            {
                file.Id,
                Path = Sanitize(file.Path),
                CanonicalPath = Sanitize(file.CanonicalPath),
                file.Size,
                PathIdentityState = file.PathIdentityState.ToString(),
                HasPhysicalObjectIdentity = !string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)
            }));

    private static string FormatJournals<T>(IReadOnlyList<T> journals) =>
        journals.Count == 0
            ? "none"
            : JsonSerializer.Serialize(journals.Select(journal => new
            {
                OperationId = GetJournalProperty(journal, "OperationId"),
                State = GetJournalProperty(journal, "State"),
                DestinationPath = Sanitize(GetJournalProperty(journal, "DestinationPath")),
                Error = Sanitize(GetJournalProperty(journal, "Error"))
            }));

    private static string? GetJournalProperty<T>(T journal, string propertyName) =>
        typeof(T).GetProperty(propertyName)?.GetValue(journal)?.ToString();

    private static bool ContainsSensitiveKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("api-key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("url", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "none";
        }

        var withoutUrls = UrlPattern().Replace(value, "[URL REDACTED]");
        return SensitiveValuePattern().Replace(withoutUrls, "$1$2[REDACTED]");
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"(?i)(api[_-]?key|password|secret|credential|token)(\s*[:=]\s*)[^,\s;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValuePattern();

    private sealed record OutputProof(string Path, long Length);
}
