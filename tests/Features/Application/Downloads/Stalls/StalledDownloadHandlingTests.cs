using Listenarr.Tests.Common;
using Listenarr.Application.Downloads.Cleanup;
using Listenarr.Application.Downloads.Stalls;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Downloads.Stalls
{
    [Trait("Name", nameof(StalledDownloadHandlingTests))]
    [Trait("Category", "Application")]
    public class StalledDownloadHandlingTests : BaseTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private static readonly StalledDownloadPolicy Policy = new() { AutoSearchAlternatives = true };

        private static DownloadClientConfiguration TorrentClient(string type = "deluge") =>
            new() { Id = "c1", Name = "Client", Type = type, IsEnabled = true };

        private static Download Active(DownloadStatus status = DownloadStatus.Downloading, long total = 100, long downloaded = 10) =>
            new()
            {
                Id = "d1",
                AudiobookId = 7,
                Title = "Old Release",
                DownloadClientId = "c1",
                Status = status,
                TotalSize = total,
                DownloadedSize = downloaded,
                Metadata = new() { ["ClientDownloadId"] = "hash-old" },
            };

        [Fact]
        public void Policy_ClientOverridesWinOverGlobal()
        {
            var settings = new ApplicationSettings { StalledTimeoutMinutes = 30, StalledMaxReplacementAttempts = 5 };
            var client = TorrentClient();
            client.Settings = new() { ["stalledTimeoutMinutes"] = "10", ["stalledCleanupBehavior"] = "none" };

            var policy = StalledDownloadPolicy.Resolve(settings, client);

            Assert.Equal(10, policy.StalledTimeoutMinutes);
            Assert.Equal(5, policy.MaxReplacementAttempts);
            Assert.Equal(StalledDownloadPolicy.CleanupNone, policy.CleanupBehavior);
        }

        [Fact]
        public void Policy_DefaultsAreSafe()
        {
            var policy = StalledDownloadPolicy.Resolve(null, null);
            Assert.True(policy.Enabled);
            Assert.Equal(60, policy.StalledTimeoutMinutes);
            Assert.Equal(15, policy.MetadataTimeoutMinutes);
            Assert.Equal(3, policy.MaxReplacementAttempts);
            Assert.Equal(StalledDownloadPolicy.CleanupNone, policy.CleanupBehavior);
            Assert.False(policy.AutoSearchAlternatives);
            Assert.True(policy.IgnoreManuallyPaused);
        }

        [Fact]
        public void ZeroSpeedAloneIsNotAStall_ButNoBytesForTimeoutIs()
        {
            var d = Active();
            var c = TorrentClient();
            Assert.Equal(StallVerdict.None, StallTracker.EvaluateObserved(d, c, Policy, T0));
            Assert.Equal(StallVerdict.None, StallTracker.EvaluateObserved(d, c, Policy, T0.AddMinutes(59)));
            Assert.Equal(StallVerdict.Stalled, StallTracker.EvaluateObserved(d, c, Policy, T0.AddMinutes(61)));
        }

        [Fact]
        public void ProgressResetsTheStallClock_AndClearsState()
        {
            var d = Active();
            var c = TorrentClient();
            StallTracker.EvaluateObserved(d, c, Policy, T0);
            Assert.Equal(StallVerdict.Stalled, StallTracker.EvaluateObserved(d, c, Policy, T0.AddMinutes(61)));

            d.DownloadedSize = 50;
            Assert.Equal(StallVerdict.None, StallTracker.EvaluateObserved(d, c, Policy, T0.AddMinutes(62)));
            Assert.Null(StallTracker.GetState(d));
            Assert.Equal(StallVerdict.None, StallTracker.EvaluateObserved(d, c, Policy, T0.AddMinutes(100)));
        }

        [Fact]
        public void MetadataPending_UsesShorterTimeoutAndIsLabelledSeparately()
        {
            var d = Active(total: 0, downloaded: 0);
            var c = TorrentClient();
            StallTracker.EvaluateObserved(d, c, Policy, T0);
            Assert.Equal(StallVerdict.MetadataPending, StallTracker.EvaluateObserved(d, c, Policy, T0.AddMinutes(5)));
            Assert.Equal("MetadataPending", StallTracker.GetState(d));
            Assert.Equal(StallVerdict.Stalled, StallTracker.EvaluateObserved(d, c, Policy, T0.AddMinutes(16)));
        }

        [Fact]
        public void ManuallyPausedDownloadsAreNotStalled()
        {
            var d = Active(DownloadStatus.Paused);
            var c = TorrentClient();
            StallTracker.EvaluateObserved(d, c, Policy, T0);
            Assert.Equal(StallVerdict.None, StallTracker.EvaluateObserved(d, c, Policy, T0.AddHours(5)));
        }

        [Theory]
        [InlineData("sabnzbd")]
        [InlineData("nzbget")]
        public void UsenetClientsNeverUseTorrentStallLogic(string type)
        {
            var d = Active();
            var c = TorrentClient(type);
            StallTracker.EvaluateObserved(d, c, Policy, T0);
            Assert.Equal(StallVerdict.None, StallTracker.EvaluateObserved(d, c, Policy, T0.AddHours(10)));
        }

        [Fact]
        public void TrackingSurvivesRestart_BecauseStateLivesInMetadata()
        {
            var d = Active();
            var c = TorrentClient();
            StallTracker.EvaluateObserved(d, c, Policy, T0);

            var reloaded = new Download
            {
                Id = d.Id,
                Status = d.Status,
                DownloadedSize = d.DownloadedSize,
                TotalSize = d.TotalSize,
                Metadata = new Dictionary<string, object>(d.Metadata),
            };
            Assert.Equal(StallVerdict.Stalled, StallTracker.EvaluateObserved(reloaded, c, Policy, T0.AddMinutes(90)));
        }

        [Fact]
        public void MissingFromClient_IsDiagnosticOnly_NeverStalled()
        {
            var d = Active();
            var c = TorrentClient();
            Assert.Equal(StallVerdict.None, StallTracker.EvaluateMissing(d, c, Policy, T0));
            Assert.Equal(StallVerdict.MissingFromClient, StallTracker.EvaluateMissing(d, c, Policy, T0.AddMinutes(20)));
            Assert.NotEqual("Stalled", StallTracker.GetState(d));
        }

        private sealed class Fixture
        {
            public Mock<IConfigurationService> Config = new();
            public Mock<IDownloadRepository> Repo = new();
            public Mock<IDownloadClientGateway> Gateway = new();
            public Mock<IRootFolderService> Roots = new();
            public DownloadQueueRemovalService Service;

            public Fixture(Download d, params string[] roots)
            {
                Config.Setup(c => c.GetDownloadClientConfigurationAsync("c1")).ReturnsAsync(TorrentClient());
                Repo.Setup(r => r.FindAsync(d.Id)).ReturnsAsync(d);
                Repo.Setup(r => r.GetAllAsync()).ReturnsAsync([d]);
                Gateway.Setup(g => g.RemoveAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
                Roots.Setup(r => r.GetAllAsync()).ReturnsAsync(roots.Select(p => new RootFolder { Path = p }).ToList());
                Service = new DownloadQueueRemovalService(Config.Object, Repo.Object, Gateway.Object, Roots.Object, new DownloadEventRecorder(Events, NullLogger<DownloadEventRecorder>.Instance), NullLogger<DownloadQueueRemovalService>.Instance);
            }

            public FakeEvents Events { get; } = new();

            public void GatewayThrows(string message) =>
                Gateway.Setup(g => g.RemoveAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException(message));

            public void VerifyGatewayCalls(Times times, bool? delete = null) =>
                Gateway.Verify(g => g.RemoveAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<string>(), delete ?? It.IsAny<bool>(), It.IsAny<CancellationToken>()), times);
        }

        [Fact]
        public async Task Remove_ListenarrOnly_DoesNotContactClient()
        {
            var f = new Fixture(Active());
            var result = await f.Service.RemoveAsync("d1", null, DownloadRemovalMode.ListenarrOnly);
            Assert.True(result.Success);
            f.VerifyGatewayCalls(Times.Never());
            f.Repo.Verify(r => r.RemoveAsync("d1"), Times.Once);
        }

        [Theory]
        [InlineData(DownloadRemovalMode.ClientKeepFiles, false)]
        [InlineData(DownloadRemovalMode.ClientDeleteData, true)]
        public async Task Remove_ClientModes_PassCorrectDeleteFlagAndStoredHash(DownloadRemovalMode mode, bool delete)
        {
            var f = new Fixture(Active(), "/data");
            var result = await f.Service.RemoveAsync("d1", "c1", mode);
            Assert.True(result.Success);
            f.Gateway.Verify(g => g.RemoveAsync(It.IsAny<DownloadClientConfiguration>(), "hash-old", delete, It.IsAny<CancellationToken>()), Times.Once);
            f.Repo.Verify(r => r.RemoveAsync("d1"), Times.Once);
        }

        [Fact]
        public async Task Remove_ClientFailure_KeepsListenarrRecordAndReportsFailure()
        {
            var f = new Fixture(Active());
            f.GatewayThrows("boom");
            var result = await f.Service.RemoveAsync("d1", null, DownloadRemovalMode.ClientKeepFiles);
            Assert.False(result.Success);
            Assert.Equal(DownloadRemovalErrorCodes.ClientFailed, result.ErrorCode);
            f.Repo.Verify(r => r.RemoveAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Remove_MissingRemote_ReportsSoUserCanChooseListenarrOnly()
        {
            var f = new Fixture(Active());
            f.GatewayThrows("Torrent not found");
            var result = await f.Service.RemoveAsync("d1", null, DownloadRemovalMode.ClientKeepFiles);
            Assert.False(result.Success);
            Assert.Equal(DownloadRemovalErrorCodes.MissingRemote, result.ErrorCode);
            f.Repo.Verify(r => r.RemoveAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Remove_ClientIdMismatch_IsRejected()
        {
            var f = new Fixture(Active());
            var result = await f.Service.RemoveAsync("d1", "other", DownloadRemovalMode.ClientKeepFiles);
            Assert.Equal(DownloadRemovalErrorCodes.ClientMismatch, result.ErrorCode);
            f.VerifyGatewayCalls(Times.Never());
        }

        [Fact]
        public async Task Remove_DeleteData_RefusedWhenDownloadPathOverlapsLibraryRoot()
        {
            var d = Active();
            d.DownloadPath = "/data/Audiobooks/Some Book";
            var f = new Fixture(d, "/data");
            var result = await f.Service.RemoveAsync("d1", null, DownloadRemovalMode.ClientDeleteData);
            Assert.Equal(DownloadRemovalErrorCodes.UnsafeDelete, result.ErrorCode);
            f.VerifyGatewayCalls(Times.Never());
        }

        [Fact]
        public async Task Remove_DeleteData_RefusedForCompletedDownloads()
        {
            var f = new Fixture(Active(DownloadStatus.Completed));
            var result = await f.Service.RemoveAsync("d1", null, DownloadRemovalMode.ClientDeleteData);
            Assert.Equal(DownloadRemovalErrorCodes.UnsafeDelete, result.ErrorCode);
            f.VerifyGatewayCalls(Times.Never());
        }

        private sealed class FakeEvents : IDownloadEventRepository
        {
            public List<DownloadEvent> Items { get; } = [];
            public bool Throw { get; set; }

            public Task AddAsync(DownloadEvent downloadEvent, CancellationToken ct = default)
            {
                if (Throw) throw new InvalidOperationException("db down");
                Items.Add(downloadEvent);
                return Task.CompletedTask;
            }

            public Task<List<DownloadEvent>> GetRecentAsync(int limit, string? downloadId = null, CancellationToken ct = default)
                => Task.FromResult(Items.Take(limit).ToList());
        }

        private sealed class FakeRejections : IReleaseRejectionRepository
        {
            public List<ReleaseRejection> Items { get; } = [];

            public Task<bool> AddAsync(ReleaseRejection rejection, CancellationToken ct = default)
            {
                if (Items.Any(i => i.AudiobookId == rejection.AudiobookId && i.ReleaseKey == rejection.ReleaseKey))
                    return Task.FromResult(false);
                Items.Add(rejection);
                return Task.FromResult(true);
            }

            public Task<List<ReleaseRejection>> ListAsync(int? audiobookId, int limit = 500, CancellationToken ct = default)
                => Task.FromResult(Items.Where(i => audiobookId == null || i.AudiobookId == audiobookId).ToList());

            public Task<bool> RemoveAsync(int id, CancellationToken ct = default) => Task.FromResult(Items.RemoveAll(i => i.Id == id) > 0);

            public Task<int> ClearAsync(int audiobookId, CancellationToken ct = default) => Task.FromResult(Items.RemoveAll(i => i.AudiobookId == audiobookId));
        }

        private sealed class FixedTime(DateTime utc) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
        }

        private sealed class ReplacementFixture
        {
            public Download Old = Active();
            public Fixture Removal;
            public Mock<IAudiobookRepository> Audiobooks = new();
            public Mock<ISearchService> Search = new();
            public Mock<IQualityProfileService> Quality = new();
            public Mock<IDownloadService> Downloads = new();
            public List<Download> History = [];
            public DownloadReplacementService Service;

            public ReplacementFixture()
            {
                Removal = new Fixture(Old);
                Removal.Repo.Setup(r => r.GetByAudiobookIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(() => History);
                Removal.Repo.Setup(r => r.GetAllAsync()).ReturnsAsync(() => [Old]);
                Audiobooks.Setup(a => a.GetByIdAsync(7)).ReturnsAsync(new Audiobook { Id = 7, Title = "Book", QualityProfile = new QualityProfile() });
                Downloads.Setup(d => d.SendToDownloadClientAsync(It.IsAny<TrustedDownloadCandidate>(), It.IsAny<string?>(), It.IsAny<int?>())).ReturnsAsync("new-1");
                Service = new DownloadReplacementService(
                    Removal.Repo.Object,
                    Audiobooks.Object,
                    Search.Object,
                    Quality.Object,
                    Downloads.Object,
                    Removal.Service,
                    Rejections,
                    new DownloadEventRecorder(Removal.Events, NullLogger<DownloadEventRecorder>.Instance),
                    new FixedTime(T0.AddHours(2)),
                    NullLogger<DownloadReplacementService>.Instance);
            }

            public FakeRejections Rejections { get; } = new();

            public void SetupResults(params SearchResult[] results)
            {
                Search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true))
                    .ReturnsAsync(results.ToList());
                Quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
                    .ReturnsAsync((List<SearchResult> r, QualityProfile _) => r.Select(x => new QualityScore { SearchResult = x, TotalScore = x.Seeders ?? 0 }).ToList());
            }

            public void MarkStalled() => Old.Metadata[StallMetadataKeys.State] = "Stalled";

            public void VerifySubmissions(Times times) =>
                Downloads.Verify(d => d.SendToDownloadClientAsync(It.IsAny<TrustedDownloadCandidate>(), It.IsAny<string?>(), It.IsAny<int?>()), times);
        }

        private static SearchResult Release(string id, string title, int seeders) =>
            new()
            {
                Id = id,
                Title = title,
                Artist = "A",
                Album = "B",
                Source = "idx",
                MagnetLink = $"magnet:?xt=urn:btih:{id}",
                Seeders = seeders,
                Size = 1,
                DownloadType = "Torrent",
            };

        private static TrustedDownloadCandidate Candidate() => TrustedDownloadCandidateFactory.Create(Release("9", "New Release", 1));

        [Fact]
        public async Task AutoReplace_PicksBestAlternativeExcludingCurrentAndFailedReleases()
        {
            var f = new ReplacementFixture();
            f.MarkStalled();
            f.History.Add(new Download { Id = "old-failed", Status = DownloadStatus.Failed, Title = "Bad Release", AudiobookId = 7 });
            f.SetupResults(Release("1", "Old Release", 99), Release("2", "Bad Release", 50), Release("3", "Good Release", 5));

            var outcome = await f.Service.TryAutoReplaceAsync("d1", TorrentClient(), Policy);

            Assert.True(outcome.Success);
            f.Downloads.Verify(d => d.SendToDownloadClientAsync(It.Is<TrustedDownloadCandidate>(c => c.Title == "Good Release"), "c1", 7), Times.Once);
            Assert.Equal(DownloadStatus.Failed, f.Old.Status);
            Assert.Equal(1, StallTracker.GetReplacementAttempts(f.Old));
            f.Removal.Gateway.Verify(g => g.RemoveAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task AutoReplace_NothingEligible_LeavesDownloadStalledAndDoesNotRemoveIt()
        {
            var f = new ReplacementFixture();
            f.MarkStalled();
            f.SetupResults(Release("1", "Old Release", 99));

            var outcome = await f.Service.TryAutoReplaceAsync("d1", TorrentClient(), Policy);

            Assert.Equal(ReplacementOutcomeKind.NoEligibleRelease, outcome.Kind);
            Assert.Equal(DownloadStatus.Downloading, f.Old.Status);
            f.Removal.VerifyGatewayCalls(Times.Never());
            Assert.NotNull(f.Old.GetMetadataString(StallMetadataKeys.NextReplacementAt));
        }

        [Fact]
        public async Task AutoReplace_StopsAtAttemptLimit()
        {
            var f = new ReplacementFixture();
            f.MarkStalled();
            f.Old.Metadata[StallMetadataKeys.ReplacementAttempts] = "3";
            f.SetupResults(Release("3", "Good Release", 5));

            var outcome = await f.Service.TryAutoReplaceAsync("d1", TorrentClient(), Policy);

            Assert.Equal(ReplacementOutcomeKind.NotAllowed, outcome.Kind);
            f.VerifySubmissions(Times.Never());
        }

        [Fact]
        public async Task AutoReplace_DoesNothingWhenNotStalledOrDisabled()
        {
            var f = new ReplacementFixture();
            f.SetupResults(Release("3", "Good Release", 5));
            Assert.Equal(ReplacementOutcomeKind.Skipped, (await f.Service.TryAutoReplaceAsync("d1", TorrentClient(), Policy)).Kind);

            f.MarkStalled();
            var off = Policy with { AutoSearchAlternatives = false };
            Assert.Equal(ReplacementOutcomeKind.NotAllowed, (await f.Service.TryAutoReplaceAsync("d1", TorrentClient(), off)).Kind);
            f.VerifySubmissions(Times.Never());
        }

        [Fact]
        public async Task ManualReplace_SubmissionFailure_ReportsPartialOutcome_AndRetrySkipsRemoval()
        {
            var f = new ReplacementFixture();
            f.Downloads.SetupSequence(d => d.SendToDownloadClientAsync(It.IsAny<TrustedDownloadCandidate>(), It.IsAny<string?>(), It.IsAny<int?>()))
                .ThrowsAsync(new DownloadClientSubmissionException("client down"))
                .ReturnsAsync("new-2");

            var first = await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);
            Assert.Equal(ReplacementOutcomeKind.SubmissionFailed, first.Kind);
            Assert.True(first.RemovedOld);
            Assert.Equal("SubmitFailed", f.Old.GetMetadataString(StallMetadataKeys.ReplacementState));

            var retry = await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);
            Assert.True(retry.Success);
            f.Removal.VerifyGatewayCalls(Times.Once());
        }

        [Fact]
        public async Task ManualReplace_RemovalFailure_StopsBeforeSubmitting()
        {
            var f = new ReplacementFixture();
            f.Removal.GatewayThrows("denied");

            var outcome = await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);

            Assert.Equal(ReplacementOutcomeKind.RemovalFailed, outcome.Kind);
            Assert.Equal(DownloadStatus.Downloading, f.Old.Status);
            f.VerifySubmissions(Times.Never());
        }

        [Fact]
        public async Task ManualReplace_MissingRemote_StillReplaces()
        {
            var f = new ReplacementFixture();
            f.Removal.GatewayThrows("Torrent not found");

            var outcome = await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);

            Assert.True(outcome.Success);
        }

        [Fact]
        public async Task ManualReplace_ClientIdentityMismatch_IsRejectedWithoutSideEffects()
        {
            var f = new ReplacementFixture();

            var outcome = await f.Service.ReplaceAsync("d1", "other-client", Candidate(), DownloadRemovalMode.ClientKeepFiles);

            Assert.Equal(DownloadRemovalErrorCodes.ClientMismatch, outcome.ErrorCode);
            Assert.Equal(DownloadStatus.Downloading, f.Old.Status);
            f.VerifySubmissions(Times.Never());
        }

        [Fact]
        public async Task ManualReplace_ConcurrentRequests_SubmitOnlyOnce()
        {
            var f = new ReplacementFixture();
            var gate = new TaskCompletionSource<bool>();
            f.Downloads.Setup(d => d.SendToDownloadClientAsync(It.IsAny<TrustedDownloadCandidate>(), It.IsAny<string?>(), It.IsAny<int?>()))
                .Returns(async () =>
                {
                    await gate.Task;
                    return "new-1";
                });

            var first = f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);
            await Task.Delay(200);
            var second = await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);
            gate.SetResult(true);
            var firstResult = await first;

            Assert.Equal(ReplacementOutcomeKind.AlreadyInProgress, second.Kind);
            Assert.True(firstResult.Success);
            f.VerifySubmissions(Times.Once());
        }

        [Fact]
        public async Task ManualReplace_PersistsRejection_AndItSurvivesHistoryCleanup()
        {
            var f = new ReplacementFixture();
            f.SetupResults(Release("1", "Old Release", 99), Release("3", "Good Release", 5));

            var replacement = await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);
            Assert.True(replacement.Success, replacement.Message);
            Assert.Contains(f.Rejections.Items, r => r.AudiobookId == 7 && r.ReleaseKey == ReleaseRejection.HashKey("HASH-OLD"));

            // Failed history is wiped; the durable rejection must still exclude the old release.
            f.History.Clear();
            f.Old.Status = DownloadStatus.Downloading;
            f.MarkStalled();
            f.Old.Metadata.Remove(StallMetadataKeys.ReplacementState);
            var outcome = await f.Service.TryAutoReplaceAsync("d1", TorrentClient(), Policy);

            Assert.True(outcome.Success);
            f.Downloads.Verify(d => d.SendToDownloadClientAsync(It.Is<TrustedDownloadCandidate>(c => c.Title == "Good Release"), "c1", 7), Times.AtLeastOnce);
            f.Downloads.Verify(d => d.SendToDownloadClientAsync(It.Is<TrustedDownloadCandidate>(c => c.Title == "Old Release"), It.IsAny<string?>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task ManualReplace_RemovalFailure_DoesNotBlocklist()
        {
            var f = new ReplacementFixture();
            f.Removal.GatewayThrows("client outage");

            await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles);

            Assert.Empty(f.Rejections.Items);
        }

        [Fact]
        public async Task Replacement_RecordsEvents_AndRecorderFailureNeverBreaksIt()
        {
            var f = new ReplacementFixture();
            Assert.True((await f.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles)).Success);
            Assert.Contains(f.Removal.Events.Items, e => e.EventType == DownloadEventTypes.ReplacementSelected);
            Assert.Contains(f.Removal.Events.Items, e => e.EventType == DownloadEventTypes.PreviousRemoved);
            Assert.Contains(f.Removal.Events.Items, e => e.EventType == DownloadEventTypes.ReplacementSubmitted);

            var g = new ReplacementFixture();
            g.Removal.Events.Throw = true;
            Assert.True((await g.Service.ReplaceAsync("d1", "c1", Candidate(), DownloadRemovalMode.ClientKeepFiles)).Success);
        }

        [Fact]
        public async Task Recovery_RemovingState_IsClearedWithoutTouchingClient()
        {
            var f = new ReplacementFixture();
            f.Old.Metadata[StallMetadataKeys.ReplacementState] = "Removing";

            var count = await f.Service.RecoverInterruptedAsync(CancellationToken.None);

            Assert.Equal(1, count);
            Assert.Equal("RemovalUncertain", f.Old.GetMetadataString(StallMetadataKeys.ReplacementState));
            f.Removal.VerifyGatewayCalls(Times.Never());
            f.VerifySubmissions(Times.Never());
        }

        [Fact]
        public async Task Recovery_RemovedState_BecomesRetryableSubmitFailed()
        {
            var f = new ReplacementFixture();
            f.Old.Status = DownloadStatus.Failed;
            f.Old.Metadata[StallMetadataKeys.ReplacementState] = "Removed";

            await f.Service.RecoverInterruptedAsync(CancellationToken.None);

            Assert.Equal("SubmitFailed", f.Old.GetMetadataString(StallMetadataKeys.ReplacementState));
            f.VerifySubmissions(Times.Never());
        }

        [Fact]
        public void EventRecorder_Sanitize_RemovesUrlsAndTruncates()
        {
            var s = DownloadEventRecorder.Sanitize("failed http://user:pw@host/x?apikey=SECRET and magnet:?xt=urn:btih:abc");
            Assert.DoesNotContain("SECRET", s);
            Assert.DoesNotContain("magnet", s);
            Assert.Contains("[url]", s);
            Assert.Equal(500, DownloadEventRecorder.Sanitize(new string('x', 900))!.Length);
            Assert.Null(DownloadEventRecorder.Sanitize("  "));
        }

        [Fact]
        public void ReleaseRejection_KeysAreNormalized()
        {
            Assert.Equal("hash:abcdef", ReleaseRejection.HashKey("  ABCDEF "));
            Assert.Equal(string.Empty, ReleaseRejection.HashKey(null));
            Assert.Equal(ReleaseRejection.MaxKeyLength, ReleaseRejection.TitleKey(new string('A', 500)).Length);
        }

        [Theory]
        [InlineData(4, "stalledTimeoutMinutes")]
        [InlineData(10081, "stalledTimeoutMinutes")]
        [InlineData(0, "stalledMetadataTimeoutMinutes")]
        [InlineData(11, "stalledMaxReplacementAttempts")]
        [InlineData(1441, "stalledRetryDelayMinutes")]
        [InlineData(0, "stalledMinimumProgressBytes")]
        public void SettingsValidator_RejectsOutOfRangeValues(int value, string field)
        {
            var s = new ApplicationSettings();
            switch (field)
            {
                case "stalledTimeoutMinutes": s.StalledTimeoutMinutes = value; break;
                case "stalledMetadataTimeoutMinutes": s.StalledMetadataTimeoutMinutes = value; break;
                case "stalledMaxReplacementAttempts": s.StalledMaxReplacementAttempts = value; break;
                case "stalledRetryDelayMinutes": s.StalledRetryDelayMinutes = value; break;
                default: s.StalledMinimumProgressBytes = value; break;
            }
            Assert.Throws<ArgumentException>(() => StalledSettingsValidator.Validate(s));
        }

        [Fact]
        public void SettingsValidator_AcceptsDefaultsAndRejectsBadCleanup()
        {
            StalledSettingsValidator.Validate(new ApplicationSettings());
            StalledSettingsValidator.Validate(new ApplicationSettings { StalledTimeoutMinutes = 5, StalledCleanupBehavior = "remove_and_delete" });
            Assert.Throws<ArgumentException>(() => StalledSettingsValidator.Validate(new ApplicationSettings { StalledCleanupBehavior = "rm -rf" }));
        }
    }
}
