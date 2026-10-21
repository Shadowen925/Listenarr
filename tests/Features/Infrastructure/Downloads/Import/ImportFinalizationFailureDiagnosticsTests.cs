using System.Text.Json;
using Listenarr.Infrastructure.Downloads.Import;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Import;

public sealed class ImportFinalizationFailureDiagnosticsTests : BaseTests
{
    [Fact]
    public async Task DescribeAsync_IncludesImportEvidenceAndRedactsSensitiveValues()
    {
        await AddAuthorizedRootAsync(FileService.GetTempPath());
        var audiobook = await CreateAudiobook();
        var outputPath = await FileService.GetFileAsync(
            audiobook.BasePath!,
            "diagnostic-output.m4b");
        Assert.True(await _provider.GetRequiredService<IAudiobookFileService>()
            .EnsureAudiobookFileAsync(audiobook, outputPath, "test"));

        var client = await CreateDownloadClientConfiguration();
        var download = await _downloadRepository.AddAsync(new DownloadBuilder()
            .WithAudiobook(audiobook)
            .WithDownloadClientConfiguration(client)
            .WithStatus(DownloadStatus.ImportPending)
            .WithPath(FileService.GetTempPath())
            .Build());
        var job = new DownloadProcessingJobBuilder()
            .WithDownload(download)
            .WithStatus(ProcessingJobStatus.Pending)
            .Build();
        job.ErrorMessage = "Finalization retry; token=diagnostic-secret";
        job.AddLogEntry("Source URL: https://example.invalid/private-download");
        job.JobData[ImportFinalizationService.ImportedAudioOutputsJobDataKey] =
            JsonSerializer.Serialize(new[]
            {
                new { Path = outputPath, Length = new FileInfo(outputPath).Length }
            });
        job.SetCheckpoint("FilesImported", 1);
        await _downloadProcessingJobRepository.AddAsync(job);

        var diagnostics = await ImportFinalizationFailureDiagnostics.DescribeAsync(
            _provider,
            _downloadRepository,
            _downloadProcessingJobRepository,
            download.Id,
            job.Id,
            audiobook.Id);

        Assert.Contains($"DownloadId: {download.Id}", diagnostics, StringComparison.Ordinal);
        Assert.Contains("DownloadStatus: ImportPending", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"ProcessingJobId: {job.Id}", diagnostics, StringComparison.Ordinal);
        Assert.Contains("ProcessingJobStatus: Pending", diagnostics, StringComparison.Ordinal);
        Assert.Contains("RetryCount: 0", diagnostics, StringComparison.Ordinal);
        Assert.Contains("ErrorMessage: Finalization retry; token=[REDACTED]", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"FilesImported=True", diagnostics, StringComparison.Ordinal);
        var serializedOutputPath = JsonSerializer.Serialize(outputPath)[1..^1];
        Assert.Contains($"\"Path\":\"{serializedOutputPath}\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"\"Length\":{new FileInfo(outputPath).Length}", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"OwnershipVerification[{outputPath}]", diagnostics, StringComparison.Ordinal);
        Assert.Contains("OutstandingFileMutationJournals: none", diagnostics, StringComparison.Ordinal);
        Assert.Contains("OutstandingCompatibilityPublicationJournals: none", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-secret", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.invalid/private-download", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[URL REDACTED]", diagnostics, StringComparison.Ordinal);
    }
}
