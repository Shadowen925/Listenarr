namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private sealed record CompatibilityStagedTargetResult(
        CompatibilityFilePublicationJournal Journal,
        FilePublicationPreparationResult? Failure);

    private async Task<CompatibilityStagedTargetResult>
        PublishCompatibilityStagedTargetAsync(
            FilePublicationPlan plan,
            FileMoveGateLease gate,
            CompatibilityFilePublicationJournal journal,
            string source,
            string destination,
            FilePublicationSourceProof sourceProof,
            CancellationToken cancellationToken)
    {
        var stagePath = Path.Combine(
            Path.GetDirectoryName(gate.DestinationPath)!,
            $".staging-{journal.OperationId:N}.partial");
        if (journal.State == CompatibilityFilePublicationState.Planned)
        {
            if (File.Exists(gate.DestinationPath))
            {
                await MarkCompatibilityNeedsAttentionAsync(
                    journal.OperationId,
                    "A compatibility destination appeared before target verification. It was preserved without overwrite.",
                    cancellationToken);
                return Blocked(
                    "ambiguous_existing_target",
                    "The destination appeared during compatibility publication and was preserved for manual review.");
            }

            using var sourceStream = OpenCompatibilityRead(gate.SourcePath);
            if (!await CompatibilityStreamMatchesAsync(
                    sourceStream,
                    sourceProof.Length,
                    sourceProof.Sha256,
                    cancellationToken))
            {
                await MarkCompatibilityNeedsAttentionAsync(
                    journal.OperationId,
                    "The compatibility source content changed before publication.",
                    cancellationToken);
                return Blocked(
                    "source_content_changed",
                    "The source changed before compatibility publication.");
            }

            if (File.Exists(stagePath))
            {
                File.Delete(stagePath);
            }

            await using (var created = new FileStream(
                             stagePath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.Read,
                             bufferSize: 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await created.FlushAsync(cancellationToken);
                created.Flush(flushToDisk: true);
            }

            journal = await _compatibilityFilePublicationJournalStore!.AdvanceAsync(
                journal.OperationId,
                CompatibilityFilePublicationState.TargetStaged,
                sourceProof.Length,
                sourceProof.Sha256,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        if (journal.State == CompatibilityFilePublicationState.TargetStaged)
        {
            if (File.Exists(gate.DestinationPath))
            {
                if (!CompatibilityTargetMatches(
                        gate.DestinationPath,
                        journal.SourceLength,
                        journal.SourceSha256))
                {
                    await MarkCompatibilityNeedsAttentionAsync(
                        journal.OperationId,
                        "A different compatibility destination appeared during staged publication.",
                        cancellationToken);
                    return Blocked(
                        "ambiguous_existing_target",
                        "The destination appeared during staged publication and was preserved for manual review.");
                }
            }
            else
            {
                if (!File.Exists(stagePath))
                {
                    await MarkCompatibilityNeedsAttentionAsync(
                        journal.OperationId,
                        "The staged compatibility destination is missing.",
                        cancellationToken);
                    return Blocked(
                        "staged_target_missing",
                        "The staged destination is missing and requires manual review.");
                }

                using var sourceStream = OpenCompatibilityRead(gate.SourcePath);
                if (!await CompatibilityStreamMatchesAsync(
                        sourceStream,
                        sourceProof.Length,
                        sourceProof.Sha256,
                        cancellationToken))
                {
                    await MarkCompatibilityNeedsAttentionAsync(
                        journal.OperationId,
                        "The compatibility source content changed before staged publication.",
                        cancellationToken);
                    return Blocked(
                        "source_content_changed",
                        "The source changed before compatibility publication.");
                }

                await using (var staged = new FileStream(
                                 stagePath,
                                 FileMode.Open,
                                 FileAccess.ReadWrite,
                                 FileShare.Read,
                                 bufferSize: 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    staged.SetLength(0);
                    sourceStream.Position = 0;
                    await sourceStream.CopyToAsync(
                        staged,
                        128 * 1024,
                        cancellationToken);
                    await staged.FlushAsync(cancellationToken);
                    staged.Flush(flushToDisk: true);
                    if (!await CompatibilityStreamMatchesAsync(
                            staged,
                            sourceProof.Length,
                            sourceProof.Sha256,
                            cancellationToken))
                    {
                        await MarkCompatibilityNeedsAttentionAsync(
                            journal.OperationId,
                            "The staged compatibility destination failed content verification and was preserved.",
                            cancellationToken);
                        return Blocked(
                            "target_verification_failed",
                            "The staged destination could not be verified and was preserved for manual review.");
                    }
                }

                PreserveCompatibilityMetadata(gate.SourcePath, stagePath);
                File.Move(stagePath, gate.DestinationPath);
            }

            journal = await _compatibilityFilePublicationJournalStore!.AdvanceAsync(
                journal.OperationId,
                CompatibilityFilePublicationState.TargetVerified,
                journal.SourceLength,
                journal.SourceSha256,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        return new CompatibilityStagedTargetResult(journal, null);

        CompatibilityStagedTargetResult Blocked(string code, string message) =>
            new(
                journal,
                CompatibilityBlocked(
                    plan,
                    source,
                    destination,
                    code,
                    message));
    }
}
