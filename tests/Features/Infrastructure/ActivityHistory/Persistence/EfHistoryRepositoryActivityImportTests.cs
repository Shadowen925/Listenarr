using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.ActivityHistory.Persistence;

[Trait("Name", nameof(EfHistoryRepositoryActivityImportTests))]
[Trait("Category", "ActivityHistory")]
public sealed class EfHistoryRepositoryActivityImportTests : BaseTests
{
    [Fact]
    public async Task GetSucceededImportedDownloadIdsAsync_ReturnsDistinctVerifiedIds()
    {
        await _historyRepository.AddAsync(new History
        {
            EventType = "Imported",
            Outcome = HistoryOutcome.Succeeded,
            DownloadId = "verified-download"
        });
        await _historyRepository.AddAsync(new History
        {
            EventType = "Imported",
            Outcome = HistoryOutcome.Succeeded,
            DownloadId = "verified-download"
        });
        await _historyRepository.AddAsync(new History
        {
            EventType = "Imported",
            Outcome = HistoryOutcome.Failed,
            DownloadId = "failed-download"
        });

        var downloadIds = await _historyRepository.GetSucceededImportedDownloadIdsAsync();

        Assert.Equal(["verified-download"], downloadIds);
    }
}
