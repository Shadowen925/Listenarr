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
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Downloads.Import
{
    public sealed class ImportFinalizationService(
        IDbContextFactory<ListenArrDbContext> dbFactory,
        IAudiobookFilePathIdentityResolver pathIdentityResolver,
        IFileSystemSemanticsResolver semanticsResolver) : IImportFinalizationService
    {
        public const string ImportedAudioOutputsJobDataKey = "ImportedAudioOutputs";

        public async Task FinalizeAsync(
            string jobId,
            string downloadId,
            int audiobookId,
            string title,
            string downloadClientId,
            string correlationId,
            bool? sourceRetained,
            Dictionary<string, object>? details = null,
            CancellationToken ct = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var job = await db.DownloadProcessingJobs.FindAsync([jobId], ct)
                ?? throw new InvalidOperationException($"Import job {jobId} no longer exists");
            var download = await db.Downloads.FindAsync([downloadId], ct)
                ?? throw new InvalidOperationException($"Download {downloadId} no longer exists");

            if (job.Status == ProcessingJobStatus.Completed && download.Status == DownloadStatus.Moved)
            {
                return;
            }

            if (!job.HasCheckpoint("FilesImported") ||
                !job.HasCheckpoint("ClientMarkedImported") ||
                !job.HasCheckpoint("ScanEnqueued"))
            {
                throw new InvalidOperationException($"Import job {jobId} cannot be finalized before all checkpoints complete");
            }

            await VerifyImportedAudioOutputsAsync(
                db,
                job,
                download,
                audiobookId,
                ct);

            var durableDetails = details == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(details);
            durableDetails["SourceRetentionKnown"] = sourceRetained.HasValue;
            if (sourceRetained.HasValue)
            {
                durableDetails[Download.SourceRetainedMetadataKey] = sourceRetained.Value;
            }

            var alreadyRecorded = await db.History.AnyAsync(
                h => h.CorrelationId == correlationId &&
                     h.EventType == HistoryEvents.Imported &&
                     h.Outcome == HistoryOutcome.Succeeded,
                ct);
            if (!alreadyRecorded)
            {
                db.History.Add(new History
                {
                    AudiobookId = audiobookId,
                    AudiobookTitle = title,
                    SourceTitle = title,
                    DownloadId = downloadId.ToUpperInvariant(),
                    DownloadClientId = downloadClientId,
                    EventType = HistoryEvents.Imported,
                    Outcome = HistoryOutcome.Succeeded,
                    Source = "DownloadImport",
                    Message = "Download import committed",
                    Timestamp = DateTime.UtcNow,
                    CorrelationId = correlationId,
                    Data = JsonSerializer.Serialize(durableDetails)
                });
            }

            download.Imported();
            download.ActiveAudiobookDeduplicationKey = null;
            download.LastImportedAt = DateTime.UtcNow;
            if (sourceRetained.HasValue)
            {
                download.SetMetadata(Download.SourceRetainedMetadataKey, sourceRetained.Value);
            }
            else
            {
                download.Metadata.Remove(Download.SourceRetainedMetadataKey);
            }
            job.MarkAsCompleted();
            job.ActiveDeduplicationKey = null;
            job.SetCheckpoint("ImportCommitted");
            if (!string.IsNullOrWhiteSpace(job.QuickAddOperationId))
            {
                var operation = await db.QuickAddOperations.FindAsync(
                    [job.QuickAddOperationId],
                    ct);
                if (operation != null)
                {
                    operation.State = QuickAddOperationState.Imported;
                    operation.FailureReason = null;
                    operation.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        private async Task VerifyImportedAudioOutputsAsync(
            ListenArrDbContext db,
            DownloadProcessingJob job,
            Download download,
            int audiobookId,
            CancellationToken cancellationToken)
        {
            if (download.AudiobookId != audiobookId)
            {
                throw new InvalidOperationException(
                    $"Download {download.Id} is not associated with audiobook {audiobookId}.");
            }

            if (!job.TryGetJobDataString(ImportedAudioOutputsJobDataKey, out var serializedOutputs))
            {
                throw new InvalidOperationException(
                    $"Import job {job.Id} has no durable proof of its imported audio outputs.");
            }

            List<ImportedAudioOutputProof>? outputs;
            try
            {
                outputs = JsonSerializer.Deserialize<List<ImportedAudioOutputProof>>(
                    serializedOutputs);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Import job {job.Id} has invalid audio output proof.",
                    exception);
            }

            if (outputs == null || outputs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Import job {job.Id} contains no imported audio outputs.");
            }

            var audiobook = await db.Audiobooks
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == audiobookId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Audiobook {audiobookId} no longer exists for import job {job.Id}.");
            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                throw new InvalidOperationException(
                    $"Audiobook {audiobookId} has no library base path for import verification.");
            }

            var rootResolution = await semanticsResolver.ResolveAsync(
                audiobook.BasePath,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
            if (rootResolution.State != PathIdentityState.Valid)
            {
                throw new InvalidOperationException(
                    $"Audiobook {audiobookId} library path cannot be safely verified: {rootResolution.Reason ?? "filesystem identity is unavailable"}");
            }

            var semantics = rootResolution.Semantics;
            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    audiobook.BasePath,
                    out var rootSyntax)
                || rootSyntax != semantics.Syntax)
            {
                throw new InvalidOperationException(
                    $"Audiobook {audiobookId} library path is not an absolute path for the current filesystem.");
            }

            var canonicalRoot = FileSystemPathIdentity.Canonicalize(
                audiobook.BasePath,
                semantics.Syntax);
            var canonicalOutputs = new HashSet<string>(semantics.Comparer);
            var registeredFiles = await db.AudiobookFiles
                .AsNoTracking()
                .Where(file => file.AudiobookId == audiobookId)
                .ToListAsync(cancellationToken);

            foreach (var output in outputs)
            {
                if (string.IsNullOrWhiteSpace(output.Path)
                    || output.Length <= 0
                    || !FileUtils.IsAudioFile(output.Path))
                {
                    throw new InvalidOperationException(
                        $"Import job {job.Id} contains an invalid audio output proof.");
                }

                string absolutePath;
                try
                {
                    absolutePath = Path.GetFullPath(output.Path);
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or NotSupportedException
                        or IOException)
                {
                    throw new InvalidOperationException(
                        $"Import job {job.Id} contains an invalid audio output path.",
                        exception);
                }

                if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                        absolutePath,
                        out var outputSyntax)
                    || outputSyntax != semantics.Syntax)
                {
                    throw new InvalidOperationException(
                        $"Imported audio output {output.Path} does not use the library filesystem path syntax.");
                }

                var canonicalPath = FileSystemPathIdentity.Canonicalize(
                    absolutePath,
                    semantics.Syntax);
                if (!canonicalOutputs.Add(canonicalPath))
                {
                    throw new InvalidOperationException(
                        $"Import job {job.Id} contains duplicate audio output paths.");
                }

                if (!FileSystemPathIdentity.IsSameOrInside(
                        canonicalPath,
                        canonicalRoot,
                        semantics))
                {
                    throw new InvalidOperationException(
                        $"Imported audio output {output.Path} is outside audiobook {audiobookId}'s library path.");
                }

                var matchingFiles = new List<(AudiobookFile File, AudiobookFilePathIdentity Identity)>();
                foreach (var file in registeredFiles)
                {
                    if (file.PathIdentityState == PathIdentityState.Conflict
                        || string.IsNullOrWhiteSpace(file.Path))
                    {
                        continue;
                    }

                    var identity = await pathIdentityResolver.ResolveAsync(
                        audiobook,
                        file.Path,
                        cancellationToken);
                    if (identity.State == PathIdentityState.Valid
                        && FileSystemPathIdentity.AreEquivalent(
                            canonicalPath,
                            identity.CanonicalPath,
                            new FileSystemPathSemantics(
                                identity.Syntax,
                                identity.CaseSensitivity)))
                    {
                        matchingFiles.Add((file, identity));
                    }
                }

                if (matchingFiles.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Imported audio output {output.Path} is not uniquely registered to audiobook {audiobookId}.");
                }

                var registeredFile = matchingFiles[0].File;
                if (!string.IsNullOrWhiteSpace(registeredFile.CanonicalPath)
                    && !FileSystemPathIdentity.AreEquivalent(
                        canonicalPath,
                        registeredFile.CanonicalPath,
                        semantics))
                {
                    throw new InvalidOperationException(
                        $"Registered audio output {output.Path} has a stale persisted path identity.");
                }

                try
                {
                    using var registrationLease = PinnedAudiobookFileRegistrationLease.Open(
                        absolutePath,
                        registeredFile.PhysicalObjectIdentity);
                    if (!registrationLease.MatchesCurrentPublication())
                    {
                        throw new InvalidOperationException(
                            $"Imported audio output {output.Path} is no longer the published file generation.");
                    }

                    using var stream = registrationLease.OpenMetadataReadStream();
                    if (!stream.CanRead
                        || !stream.CanSeek
                        || stream.Length != output.Length
                        || (registeredFile.Size.HasValue
                            && registeredFile.Size.Value > 0
                            && registeredFile.Size.Value != output.Length)
                        || stream.ReadByte() < 0)
                    {
                        throw new InvalidOperationException(
                            $"Imported audio output {output.Path} is empty, unreadable, or changed after registration.");
                    }
                }
                catch (IOException exception)
                {
                    throw new InvalidOperationException(
                        $"Imported audio output {output.Path} could not be read for finalization.",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new InvalidOperationException(
                        $"Imported audio output {output.Path} is not accessible for finalization.",
                        exception);
                }
            }

            var verifiedOutputPaths = canonicalOutputs.ToList();
            var hasUnresolvedFileMutation = await db.FileMutationJournals
                .AsNoTracking()
                .AnyAsync(
                    journal => (journal.AudiobookId == audiobookId
                            || verifiedOutputPaths.Contains(journal.DestinationPath))
                        && journal.State != FileMutationJournalState.Completed,
                    cancellationToken);
            if (hasUnresolvedFileMutation)
            {
                throw new InvalidOperationException(
                    $"Audiobook {audiobookId} has an unresolved file mutation journal.");
            }

            var hasUnresolvedCompatibilityPublication = await db.CompatibilityFilePublicationJournals
                .AsNoTracking()
                .AnyAsync(
                    journal => (journal.AudiobookId == audiobookId
                            || verifiedOutputPaths.Contains(journal.DestinationPath))
                        && journal.State != CompatibilityFilePublicationState.Completed,
                    cancellationToken);
            if (hasUnresolvedCompatibilityPublication)
            {
                throw new InvalidOperationException(
                    $"Audiobook {audiobookId} has an unresolved file publication journal.");
            }
        }

        private sealed record ImportedAudioOutputProof(string Path, long Length);
    }
}
