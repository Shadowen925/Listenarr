/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.Json;
using Listenarr.Infrastructure.Downloads.Import;
using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Import
{
    public sealed class ImportFinalizationServiceTests : BaseTests
    {
        [Fact]
        public async Task FinalizeAsync_RequiresEveryPostImportCheckpoint()
        {
            var audiobook = await CreateAudiobook();
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithStatus(ProcessingJobStatus.Processing)
                .Build();
            job.SetCheckpoint("FilesImported");
            await _downloadProcessingJobRepository.AddAsync(job);

            var service = _provider.GetRequiredService<IImportFinalizationService>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-checkpoints", sourceRetained: null));

            Assert.Equal(DownloadStatus.ImportPending, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Equal(ProcessingJobStatus.Processing, (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!.Status);
        }

        [Fact]
        public async Task FinalizeAsync_CommitsDownloadJobAndHistoryIdempotently()
        {
            var audiobook = await CreateAudiobook();
            var importedOutput = await RegisterAudioOutputAsync(audiobook);
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithStatus(ProcessingJobStatus.Processing)
                .Build();
            job.SetCheckpoint("FilesImported");
            job.SetCheckpoint("ClientMarkedImported");
            job.SetCheckpoint("ScanEnqueued", Guid.NewGuid().ToString());
            job.JobData[ImportFinalizationService.ImportedAudioOutputsJobDataKey] =
                JsonSerializer.Serialize(new[] { importedOutput });
            await _downloadProcessingJobRepository.AddAsync(job);

            var service = _provider.GetRequiredService<IImportFinalizationService>();
            job = (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!;
            await service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-success", sourceRetained: true);
            await service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-success", sourceRetained: true);

            var finalizedDownload = (await _downloadRepository.GetByIdAsync(download.Id))!;
            Assert.Equal(DownloadStatus.Moved, finalizedDownload.Status);
            Assert.Equal(
                bool.TrueString,
                finalizedDownload.GetMetadataString(Download.SourceRetainedMetadataKey));
            Assert.Equal(ProcessingJobStatus.Completed, (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!.Status);
            var page = await _historyRepository.QueryAsync(new HistoryQuery
            {
                CorrelationId = "finalization-success"
            });
            var imported = Assert.Single(page.Records, entry =>
                entry.EventType == HistoryEvents.Imported &&
                entry.Outcome == HistoryOutcome.Succeeded);
            using var details = JsonDocument.Parse(imported.Data!);
            Assert.True(details.RootElement.GetProperty("SourceRetentionKnown").GetBoolean());
            Assert.True(details.RootElement.GetProperty(Download.SourceRetainedMetadataKey).GetBoolean());
        }

        [Fact]
        public async Task FinalizeAsync_RejectsMissingDurableAudioOutputProof()
        {
            var audiobook = await CreateAudiobook();
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = CreateFinalizableJob(download);
            await _downloadProcessingJobRepository.AddAsync(job);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _provider.GetRequiredService<IImportFinalizationService>().FinalizeAsync(
                    job.Id,
                    download.Id,
                    audiobook.Id,
                    audiobook.Title ?? download.Title,
                    client.Id,
                    "finalization-no-output-proof",
                    sourceRetained: null));

            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
        }

        [Fact]
        public async Task FinalizeAsync_RequiresEveryPersistedOutputToBeRegistered()
        {
            var audiobook = await CreateAudiobook();
            var registeredOutput = await RegisterAudioOutputAsync(audiobook);
            var unregisteredOutput = await CreateAudioOutputAsync(audiobook);
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = CreateFinalizableJob(download);
            job.JobData[ImportFinalizationService.ImportedAudioOutputsJobDataKey] =
                JsonSerializer.Serialize(new[] { registeredOutput, unregisteredOutput });
            await _downloadProcessingJobRepository.AddAsync(job);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _provider.GetRequiredService<IImportFinalizationService>().FinalizeAsync(
                    job.Id,
                    download.Id,
                    audiobook.Id,
                    audiobook.Title ?? download.Title,
                    client.Id,
                    "finalization-partial-registration",
                    sourceRetained: null));

            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
        }

        [Fact]
        public async Task FinalizeAsync_BlocksCleanupWhenPublicationJournalIsUnresolved()
        {
            var audiobook = await CreateAudiobook();
            var importedOutput = await RegisterAudioOutputAsync(audiobook);
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = CreateFinalizableJob(download);
            job.JobData[ImportFinalizationService.ImportedAudioOutputsJobDataKey] =
                JsonSerializer.Serialize(new[] { importedOutput });
            await _downloadProcessingJobRepository.AddAsync(job);

            await using (var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync())
            {
                db.CompatibilityFilePublicationJournals.Add(
                    new CompatibilityFilePublicationJournal
                    {
                        OperationId = Guid.NewGuid(),
                        SourcePath = Path.Combine(audiobook.BasePath!, "source.m4b"),
                        DestinationPath = importedOutput.Path,
                        SourceSha256 = new string('a', 64),
                        State = CompatibilityFilePublicationState.TargetVerified,
                        AudiobookId = audiobook.Id
                    });
                await db.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _provider.GetRequiredService<IImportFinalizationService>().FinalizeAsync(
                    job.Id,
                    download.Id,
                    audiobook.Id,
                    audiobook.Title ?? download.Title,
                    client.Id,
                    "finalization-unresolved-publication",
                    sourceRetained: null));

            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
        }

        [Fact]
        public async Task FinalizeAsync_BlocksCleanupWhenFileMutationJournalIsUnresolved()
        {
            var audiobook = await CreateAudiobook();
            var importedOutput = await RegisterAudioOutputAsync(audiobook);
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = CreateFinalizableJob(download);
            job.JobData[ImportFinalizationService.ImportedAudioOutputsJobDataKey] =
                JsonSerializer.Serialize(new[] { importedOutput });
            await _downloadProcessingJobRepository.AddAsync(job);

            await using (var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(
                    new FileMutationJournal
                    {
                        OperationId = Guid.NewGuid(),
                        Action = FileAction.Copy,
                        SourcePath = Path.Combine(audiobook.BasePath!, "source.m4b"),
                        DestinationPath = importedOutput.Path,
                        SourceParentDirectoryObjectIdentity = "source-parent",
                        DestinationParentDirectoryObjectIdentity = "destination-parent",
                        SourcePhysicalObjectIdentity = "source-file",
                        SourceLength = importedOutput.Length,
                        State = FileMutationJournalState.TargetVerified,
                        AudiobookId = audiobook.Id
                    });
                await db.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _provider.GetRequiredService<IImportFinalizationService>().FinalizeAsync(
                    job.Id,
                    download.Id,
                    audiobook.Id,
                    audiobook.Title ?? download.Title,
                    client.Id,
                    "finalization-unresolved-file-mutation",
                    sourceRetained: null));

            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
        }

        [Fact]
        public async Task FinalizeAsync_RejectsOutputChangedAfterItWasRecorded()
        {
            var audiobook = await CreateAudiobook();
            var importedOutput = await RegisterAudioOutputAsync(audiobook);
            await File.WriteAllBytesAsync(importedOutput.Path, [1, 2, 3, 4, 5]);
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = CreateFinalizableJob(download);
            job.JobData[ImportFinalizationService.ImportedAudioOutputsJobDataKey] =
                JsonSerializer.Serialize(new[] { importedOutput });
            await _downloadProcessingJobRepository.AddAsync(job);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _provider.GetRequiredService<IImportFinalizationService>().FinalizeAsync(
                    job.Id,
                    download.Id,
                    audiobook.Id,
                    audiobook.Title ?? download.Title,
                    client.Id,
                    "finalization-changed-output",
                    sourceRetained: null));

            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
        }

        private DownloadProcessingJob CreateFinalizableJob(Download download)
        {
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithStatus(ProcessingJobStatus.Processing)
                .Build();
            job.SetCheckpoint("FilesImported");
            job.SetCheckpoint("ClientMarkedImported");
            job.SetCheckpoint("ScanEnqueued", Guid.NewGuid().ToString());
            return job;
        }

        private async Task<ImportedAudioOutput> RegisterAudioOutputAsync(Audiobook audiobook)
        {
            var output = await CreateAudioOutputAsync(audiobook);
            Assert.True(await _provider.GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(audiobook, output.Path, "test"));
            return output;
        }

        private static async Task<ImportedAudioOutput> CreateAudioOutputAsync(Audiobook audiobook)
        {
            var path = Path.Combine(
                audiobook.BasePath!,
                $"{Guid.NewGuid():N}.m4b");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            return new ImportedAudioOutput(path, new FileInfo(path).Length);
        }

        private sealed record ImportedAudioOutput(string Path, long Length);
    }
}
