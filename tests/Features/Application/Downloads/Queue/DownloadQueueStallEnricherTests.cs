using Listenarr.Application.Downloads.Stalls;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Queue
{
    [Trait("Name", "DownloadQueueStallEnricherTests")]
    [Trait("Category", "DownloadQueueStallEnricher")]
    public sealed class DownloadQueueStallEnricherTests : BaseTests
    {
        [Fact]
        [Trait("Method", "Apply")]
        public void Apply_DuplicateQueueEntries_EnrichesEveryMatchingEntry()
        {
            var client = CreateClient("client-a");
            var download = CreateStalledDownload("shared-id", client.Id);
            var items = new List<QueueItem>
            {
                new() { Id = download.Id, DownloadClientId = client.Id },
                new() { Id = download.Id, DownloadClientId = client.Id }
            };

            DownloadQueueStallEnricher.Apply(items, [download], [client], new HashSet<string> { client.Id });

            Assert.Equal(2, items.Count);
            Assert.All(items, item => Assert.Equal(nameof(StallVerdict.Stalled), item.StallState));
        }

        [Fact]
        [Trait("Method", "Apply")]
        public void Apply_CollidingIdsAcrossClients_KeepsClientOwnershipAndAddsSyntheticOnce()
        {
            var clientA = CreateClient("client-a");
            var clientB = CreateClient("client-b");
            var downloadA = CreateStalledDownload("shared-external-id", clientA.Id);
            var downloadB = new Download
            {
                Id = "tracked-download-b",
                DownloadClientId = clientB.Id,
                Status = DownloadStatus.ImportPending,
                Metadata = new Dictionary<string, object>
                {
                    ["Recovery.State"] = "NeedsAttention"
                }
            };
            var items = new List<QueueItem>
            {
                new() { Id = "shared-external-id", DownloadClientId = clientA.Id },
                new() { Id = "shared-external-id", DownloadClientId = clientB.Id }
            };

            DownloadQueueStallEnricher.Apply(
                items,
                [downloadA, downloadB, downloadB],
                [clientA, clientB],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { clientA.Id, clientB.Id });

            Assert.Equal(nameof(StallVerdict.Stalled), items.Single(item => item.DownloadClientId == clientA.Id).StallState);
            Assert.Null(items.Single(item => item.DownloadClientId == clientB.Id && item.Id == "shared-external-id").StallState);

            var syntheticItems = items.Where(item => item.Id == downloadB.Id).ToList();
            var synthetic = Assert.Single(syntheticItems);
            Assert.Equal(clientB.Id, synthetic.DownloadClientId);
            Assert.True(synthetic.MissingFromClient);
        }

        private static DownloadClientConfiguration CreateClient(string id) =>
            new()
            {
                Id = id,
                Name = id,
                Type = "qbittorrent",
                IsEnabled = true
            };

        private static Download CreateStalledDownload(string id, string clientId) =>
            new()
            {
                Id = id,
                DownloadClientId = clientId,
                Status = DownloadStatus.Downloading,
                Metadata = new Dictionary<string, object>
                {
                    [StallMetadataKeys.State] = nameof(StallVerdict.Stalled)
                }
            };
    }
}
