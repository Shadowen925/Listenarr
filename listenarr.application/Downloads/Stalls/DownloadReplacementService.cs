using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Stalls
{
    public enum ReplacementOutcomeKind
    {
        Replaced,
        NotAllowed,
        NoEligibleRelease,
        RemovalFailed,
        SubmissionFailed,
        AlreadyInProgress,
        Skipped,
    }

    public sealed record ReplacementOutcome(
        ReplacementOutcomeKind Kind,
        string Message,
        bool RemovedOld = false,
        string? NewDownloadId = null,
        string? ErrorCode = null)
    {
        public bool Success => Kind == ReplacementOutcomeKind.Replaced;
    }

    /// <summary>
    /// Replaces one tracked download with another release. Shared by automatic stall recovery and the
    /// explicit "Download This Instead" action. State is persisted on the old record's metadata at each
    /// step so a partial failure (removed but not resubmitted) can be reported and retried.
    /// </summary>
    public sealed class DownloadReplacementService(
        IDownloadRepository downloadRepository,
        IAudiobookRepository audiobookRepository,
        ISearchService searchService,
        IQualityProfileService qualityProfileService,
        IDownloadService downloadService,
        DownloadQueueRemovalService removalService,
        IReleaseRejectionRepository rejections,
        DownloadEventRecorder events,
        TimeProvider timeProvider,
        ILogger<DownloadReplacementService> logger)
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

        public async Task<ReplacementOutcome> TryAutoReplaceAsync(
            string downloadId,
            DownloadClientConfiguration client,
            StalledDownloadPolicy policy,
            CancellationToken ct = default)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var download = await downloadRepository.FindAsync(downloadId);
            if (download == null || download.AudiobookId is not int audiobookId
                || !string.Equals(download.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)
                || StallTracker.GetState(download) != nameof(StallVerdict.Stalled)
                || download.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused))
            {
                return new(ReplacementOutcomeKind.Skipped, "Download is no longer eligible for automatic replacement.");
            }

            if (!policy.AutoSearchAlternatives)
            {
                return new(ReplacementOutcomeKind.NotAllowed, "Automatic replacement is disabled.");
            }

            if (StallTracker.IsUsenetClient(client))
            {
                return new(ReplacementOutcomeKind.NotAllowed, "Automatic replacement is not supported for Usenet clients.");
            }

            var attempts = StallTracker.GetReplacementAttempts(download);
            if (attempts >= policy.MaxReplacementAttempts)
            {
                return new(ReplacementOutcomeKind.NotAllowed, $"Replacement attempt limit ({policy.MaxReplacementAttempts}) reached.");
            }

            var notBefore = StallTracker.ReadDate(download, StallMetadataKeys.NextReplacementAt);
            if (notBefore != null && now < notBefore.Value)
            {
                return new(ReplacementOutcomeKind.Skipped, "Waiting for the replacement retry delay.");
            }

            var gate = Locks.GetOrAdd(download.Id, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct))
            {
                return new(ReplacementOutcomeKind.AlreadyInProgress, "A replacement is already running for this download.");
            }

            try
            {
                var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
                if (audiobook?.QualityProfile == null)
                {
                    return await DeferAsync(download, now, policy, "The audiobook has no quality profile, so alternatives cannot be searched.");
                }

                var excluded = await GetExcludedReleasesAsync(audiobookId, download, ct);
                var results = await searchService.SearchAsync(DownloadSearchQueryBuilder.Build(audiobook), isAutomaticSearch: true);
                var scored = results.Count == 0 ? [] : await qualityProfileService.ScoreSearchResults(results, audiobook.QualityProfile);
                var best = scored
                    .Where(s => !s.IsRejected && !excluded.Matches(s.SearchResult))
                    .OrderByDescending(s => s.TotalScore)
                    .FirstOrDefault();

                if (best == null)
                {
                    return await DeferAsync(download, now, policy, "No eligible alternative release was found.");
                }

                var candidate = TrustedDownloadCandidateFactory.Create(best.SearchResult);
                var mode = policy.CleanupBehavior switch
                {
                    StalledDownloadPolicy.CleanupNone => (DownloadRemovalMode?)null,
                    StalledDownloadPolicy.CleanupRemoveAndDelete => DownloadRemovalMode.ClientDeleteData,
                    _ => DownloadRemovalMode.ClientKeepFiles,
                };

                download.Metadata[StallMetadataKeys.ReplacementAttempts] = (attempts + 1).ToString(CultureInfo.InvariantCulture);
                if (policy.AutoBlocklist)
                {
                    download.Metadata[StallMetadataKeys.Blocklisted] = true;
                }

                await events.RecordAsync(DownloadEventTypes.ReplacementSelected, download, message: $"Selected '{candidate.Title}'", releaseId: candidate.Id, ct: ct);
                return await ExecuteReplacementAsync(download, candidate, mode, null, policy.AutoBlocklist, "Stalled", ct);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Explicit replacement requested by the user from manual search.</summary>
        public async Task<ReplacementOutcome> ReplaceAsync(
            string downloadId,
            string? expectedClientId,
            TrustedDownloadCandidate candidate,
            DownloadRemovalMode mode,
            CancellationToken ct = default)
        {
            var download = await downloadRepository.FindAsync(downloadId);
            if (download == null)
            {
                return new(ReplacementOutcomeKind.NotAllowed, "The download being replaced no longer exists.", ErrorCode: DownloadRemovalErrorCodes.NotFound);
            }

            if (!string.IsNullOrWhiteSpace(expectedClientId)
                && !string.Equals(expectedClientId, download.DownloadClientId, StringComparison.OrdinalIgnoreCase))
            {
                return new(ReplacementOutcomeKind.NotAllowed, "The download client does not match the client that owns this download.", ErrorCode: DownloadRemovalErrorCodes.ClientMismatch);
            }

            var retry = download.Status == DownloadStatus.Failed
                && download.GetMetadataString(StallMetadataKeys.ReplacementState) == "SubmitFailed";
            var active = download.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused;
            if (download.GetMetadataString(StallMetadataKeys.ReplacementState) is "Removing" or "RemovalUncertain" or "Removed")
            {
                return new(ReplacementOutcomeKind.AlreadyInProgress, "A previous replacement has an unresolved removal state. Resolve it before retrying to avoid removing the wrong client item.");
            }

            if (!active && !retry)
            {
                return new(ReplacementOutcomeKind.NotAllowed, $"A download in state {download.Status} cannot be replaced.");
            }

            var gate = Locks.GetOrAdd(download.Id, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct))
            {
                return new(ReplacementOutcomeKind.AlreadyInProgress, "A replacement is already running for this download.");
            }

            try
            {
                // Re-read under the lock so a concurrent request cannot replace twice.
                download = await downloadRepository.FindAsync(downloadId) ?? download;
                var stillRetry = download.Status == DownloadStatus.Failed
                    && download.GetMetadataString(StallMetadataKeys.ReplacementState) == "SubmitFailed";
                if (download.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused) && !stillRetry)
                {
                    return new(ReplacementOutcomeKind.AlreadyInProgress, "This download was already replaced.");
                }

                await events.RecordAsync(DownloadEventTypes.ReplacementSelected, download, message: $"Selected '{candidate.Title}'", releaseId: candidate.Id, ct: ct);
                return await ExecuteReplacementAsync(
                    download,
                    candidate,
                    mode == DownloadRemovalMode.ListenarrOnly ? null : mode,
                    expectedClientId,
                    true,
                    "Manual replacement",
                    ct);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<ReplacementOutcome> ExecuteReplacementAsync(
            Download old,
            TrustedDownloadCandidate candidate,
            DownloadRemovalMode? clientMode,
            string? expectedClientId,
            bool blocklistOld,
            string rejectionReason,
            CancellationToken ct)
        {
            var resubmitOnly = old.Status == DownloadStatus.Failed
                && old.GetMetadataString(StallMetadataKeys.ReplacementState) == "SubmitFailed";
            var removedOld = resubmitOnly;
            var targetClientId = old.DownloadClientId;

            if (!resubmitOnly)
            {
                old.Metadata[StallMetadataKeys.ReplacementState] = "Removing";
                old.Metadata[StallMetadataKeys.ReplacementTarget] = candidate.Title;
                await downloadRepository.UpdateAsync(old);

                if (clientMode is { } mode)
                {
                    var removal = await removalService.RemoveFromClientAsync(old, mode, ct);
                    if (!removal.Success && removal.ErrorCode != DownloadRemovalErrorCodes.MissingRemote)
                    {
                        old.Metadata[StallMetadataKeys.ReplacementState] = "RemovalFailed";
                        old.Metadata[StallMetadataKeys.ReplacementError] = removal.Message;
                        await downloadRepository.UpdateAsync(old);
                        await events.RecordAsync(DownloadEventTypes.PreviousRemoved, old, DownloadEventOutcomes.Failure, removal.Message, ct: ct);
                        return new(ReplacementOutcomeKind.RemovalFailed, removal.Message, false, null, removal.ErrorCode);
                    }

                    await events.RecordAsync(DownloadEventTypes.PreviousRemoved, old, message: $"Removal mode {mode}", ct: ct);
                }

                // The removal is confirmed (or not requested) so the old release is now known-bad.
                // A failed removal above returns before this point, so a client outage never blocklists.
                if (blocklistOld)
                {
                    await RejectAsync(old, rejectionReason, ct);
                }

                // Moving the old row out of the active set releases the one-active-download-per-audiobook key.
                old.Failed($"Replaced by '{candidate.Title}'");
                old.Metadata[StallMetadataKeys.ReplacementState] = "Removed";
                old.Metadata.Remove(StallMetadataKeys.ReplacementError);
                await downloadRepository.UpdateAsync(old);
                removedOld = true;
            }

            try
            {
                var newId = await downloadService.SendToDownloadClientAsync(candidate, targetClientId, old.AudiobookId);
                if (string.IsNullOrEmpty(newId))
                {
                    throw new InvalidOperationException("Another active download for this audiobook blocked the submission.");
                }

                old.Metadata[StallMetadataKeys.ReplacementState] = "Submitted";
                old.Metadata[StallMetadataKeys.ReplacementTarget] = candidate.Title;
                old.Metadata["Replacement.NewDownloadId"] = newId;
                old.Metadata["Replacement.ReleaseId"] = candidate.Id;
                await downloadRepository.UpdateAsync(old);
                await TryTagNewDownloadAsync(newId, old);
                await events.RecordAsync(DownloadEventTypes.ReplacementSubmitted, old, message: $"Submitted '{candidate.Title}' as {newId}", releaseId: candidate.Id, ct: ct);
                return new(ReplacementOutcomeKind.Replaced, $"Replaced with '{candidate.Title}'.", removedOld, newId);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(ex, "Replacement submission failed for download {DownloadId}", old.Id);
                old.Metadata[StallMetadataKeys.ReplacementState] = "SubmitFailed";
                old.Metadata[StallMetadataKeys.ReplacementError] = ex.Message;
                await downloadRepository.UpdateAsync(old);
                await events.RecordAsync(DownloadEventTypes.ReplacementSubmitted, old, DownloadEventOutcomes.Failure, ex.Message, candidate.Id, ct: ct);
                return new(
                    ReplacementOutcomeKind.SubmissionFailed,
                    $"The old download was removed but '{candidate.Title}' could not be submitted: {ex.Message}. Retry the replacement to submit it again.",
                    removedOld);
            }
        }

        private async Task TryTagNewDownloadAsync(string newId, Download old)
        {
            try
            {
                var carry = StallTracker.GetReplacementAttempts(old);
                if (carry > 0)
                {
                    await downloadRepository.UpdateMetadataAsync(newId, StallMetadataKeys.ReplacementAttempts, carry.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogDebug(ex, "Could not carry replacement attempts to new download {DownloadId}", newId);
            }
        }

        private async Task<ReplacementOutcome> DeferAsync(Download download, DateTime now, StalledDownloadPolicy policy, string reason)
        {
            download.Metadata[StallMetadataKeys.Reason] = reason;
            download.Metadata[StallMetadataKeys.NextReplacementAt] = now.AddMinutes(Math.Max(1, policy.RetryDelayMinutes)).ToString("O", CultureInfo.InvariantCulture);
            await downloadRepository.UpdateAsync(download);
            return new(ReplacementOutcomeKind.NoEligibleRelease, reason);
        }

        /// <summary>
        /// Resolves replacements interrupted by a restart. A row still active in "Removing" was never
        /// confirmed removed, so the marker is cleared and normal stall evaluation continues. A row
        /// "Removed" without a submission is moved to "SubmitFailed" so the user can retry it.
        /// No client or file operations are performed.
        /// </summary>
        public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
        {
            var recovered = 0;
            foreach (var download in await downloadRepository.GetAllAsync())
            {
                ct.ThrowIfCancellationRequested();
                var state = download.GetMetadataString(StallMetadataKeys.ReplacementState);
                if (state is not ("Removing" or "Removed"))
                {
                    continue;
                }

                if (state == "Removing" && download.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused)
                {
                    download.Metadata[StallMetadataKeys.ReplacementState] = "RemovalUncertain";
                    download.Metadata[StallMetadataKeys.ReplacementError] =
                        "The application restarted while removal was in progress. The client's state was not inferred because its queue may be filtered; verify the exact client item before retrying.";
                }
                else if (state == "Removed" && download.Status == DownloadStatus.Failed)
                {
                    download.Metadata[StallMetadataKeys.ReplacementState] = "SubmitFailed";
                    download.Metadata[StallMetadataKeys.ReplacementError] = "Interrupted by an application restart before the replacement was submitted.";
                }
                else
                {
                    continue;
                }

                await downloadRepository.UpdateAsync(download);
                await events.RecordAsync(DownloadEventTypes.ReplacementSubmitted, download, DownloadEventOutcomes.Info, "Recovered an interrupted replacement after restart.", ct: ct);
                recovered++;
            }

            return recovered;
        }

        private async Task RejectAsync(Download old, string reason, CancellationToken ct)
        {
            if (old.AudiobookId is not int audiobookId)
            {
                return;
            }

            var keys = new[]
            {
                ReleaseRejection.HashKey(old.GetExternalId()),
                ReleaseRejection.HashKey(old.GetMetadataString("TorrentHash")),
                ReleaseRejection.IdKey(old.GetMetadataString("ReleaseId")),
                ReleaseRejection.TitleKey(old.Title),
            };
            foreach (var key in keys.Where(k => k.Length > 0).Distinct())
            {
                try
                {
                    await rejections.AddAsync(new ReleaseRejection
                    {
                        AudiobookId = audiobookId,
                        ReleaseKey = key,
                        ReleaseTitle = old.Title,
                        Reason = reason,
                        Source = "Replacement",
                        CreatedAt = DateTime.UtcNow,
                    }, ct);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogWarning(ex, "Could not persist release rejection for audiobook {AudiobookId}", audiobookId);
                }
            }
        }

        private async Task<ExcludedReleases> GetExcludedReleasesAsync(int audiobookId, Download current, CancellationToken ct)
        {
            var excluded = new ExcludedReleases();
            foreach (var rejection in await rejections.ListAsync(audiobookId, 1000, ct))
            {
                excluded.AddKey(rejection.ReleaseKey);
            }

            excluded.Add(current.GetMetadataString("Replacement.ReleaseId"), current.Title);
            excluded.AddHash(current.GetExternalId());
            excluded.AddHash(current.GetMetadataString("TorrentHash"));
            foreach (var previous in await downloadRepository.GetByAudiobookIdAsync(audiobookId))
            {
                var replaced = previous.GetMetadataString(StallMetadataKeys.ReplacementState) != null
                    || previous.Status == DownloadStatus.Failed
                    || previous.GetMetadataString(StallMetadataKeys.Blocklisted) != null;
                if (replaced)
                {
                    excluded.Add(previous.GetMetadataString("Replacement.ReleaseId"), previous.Title);
                    excluded.AddHash(previous.GetExternalId());
                    excluded.AddHash(previous.GetMetadataString("TorrentHash"));
                }
            }

            return excluded;
        }

        private sealed class ExcludedReleases
        {
            private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _titles = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

            public void Add(string? id, string? title)
            {
                if (!string.IsNullOrWhiteSpace(id)) _ids.Add(id);
                if (!string.IsNullOrWhiteSpace(title)) _titles.Add(title.Trim());
            }

            public void AddKey(string key) => _keys.Add(key);
            public void AddHash(string? hash)
            {
                var key = ReleaseRejection.HashKey(hash);
                if (key.Length > 0)
                {
                    _keys.Add(key);
                }
            }

            public bool Matches(SearchResult result)
            {
                var id = result.Id;
                var title = result.Title;
                var magnetHash = TryReadMagnetHash(result.MagnetLink);
                return (!string.IsNullOrWhiteSpace(id) && (_ids.Contains(id) || _keys.Contains(ReleaseRejection.IdKey(id)) || _keys.Contains(ReleaseRejection.HashKey(id))))
                    || (!string.IsNullOrWhiteSpace(title) && (_titles.Contains(title.Trim()) || _keys.Contains(ReleaseRejection.TitleKey(title))))
                    || (!string.IsNullOrWhiteSpace(magnetHash) && _keys.Contains(ReleaseRejection.HashKey(magnetHash)));
            }

            private static string? TryReadMagnetHash(string? magnet)
            {
                if (string.IsNullOrWhiteSpace(magnet))
                {
                    return null;
                }

                var match = System.Text.RegularExpressions.Regex.Match(magnet, @"btih:([a-fA-F0-9]{40}|[a-zA-Z2-7]{32})");
                return match.Success ? match.Groups[1].Value : null;
            }
        }
    }
}
