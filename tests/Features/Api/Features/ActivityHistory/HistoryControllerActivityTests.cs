using Listenarr.Api.Features.ActivityHistory;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.ActivityHistory;

[Trait("Name", nameof(HistoryControllerActivityTests))]
[Trait("Category", "ActivityHistory")]
public sealed class HistoryControllerActivityTests : BaseTests
{
    [Fact]
    public async Task DismissActivityEntries_PersistsNonDestructiveHistoryRecord()
    {
        var history = new Mock<IHistoryRepository>();
        history
            .Setup(repository => repository.GetByEventTypeAsync(
                "ActivityDismissed",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        history
            .Setup(repository => repository.AddAsync(
                It.IsAny<History>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((History entry, CancellationToken _) => entry);
        var controller = CreateController(history.Object);

        var result = await controller.DismissActivityEntries(new HistoryController.ActivityDismissalRequest(
        [
            new HistoryController.ActivityDismissalEntry(
                "download:deluge:client-1:download-1",
                42,
                "Book",
                "download-1",
                "failed:v1")
        ]));

        Assert.IsType<OkObjectResult>(result);
        history.Verify(repository => repository.AddAsync(
            It.Is<History>(entry =>
                entry.EventType == "ActivityDismissed"
                && entry.SourceTitle == "download:deluge:client-1:download-1"
                && entry.DownloadId == "download-1"
                && entry.AudiobookId == 42
                && entry.IdempotencyKey!.StartsWith("activity-dismiss:", StringComparison.Ordinal)
                && entry.Data!.Contains("\"version\":\"failed:v1\"", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        history.Verify(repository => repository.DeleteAsync(
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DismissActivityEntries_IsIdempotentForAnAlreadyDismissedKey()
    {
        var history = new Mock<IHistoryRepository>();
        history
            .Setup(repository => repository.GetByEventTypeAsync(
                "ActivityDismissed",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new History
                {
                    EventType = "ActivityDismissed",
                    SourceTitle = "quick-add:42",
                    Data = "{\"version\":\"failed:v1\"}"
                }
            ]);
        var controller = CreateController(history.Object);

        var result = await controller.DismissActivityEntries(new HistoryController.ActivityDismissalRequest(
        [
            new HistoryController.ActivityDismissalEntry("quick-add:42", 42, "Book", null, "failed:v1")
        ]));

        Assert.IsType<OkObjectResult>(result);
        history.Verify(repository => repository.AddAsync(
            It.IsAny<History>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetActivityDismissals_ReturnsPersistedKeyVersions()
    {
        var history = new Mock<IHistoryRepository>();
        history
            .Setup(repository => repository.GetByEventTypeAsync(
                "ActivityDismissed",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new History { SourceTitle = "quick-add:42" },
                new History { SourceTitle = "quick-add:42" },
                new History { SourceTitle = null }
            ]);
        var controller = CreateController(history.Object);

        var result = Assert.IsType<OkObjectResult>(await controller.GetActivityDismissals());

        Assert.Equal(
            [new HistoryController.ActivityDismissalVersion("quick-add:42", "")],
            Assert.IsType<HistoryController.ActivityDismissalVersion[]>(result.Value));
    }

    [Fact]
    public async Task DismissActivityEntries_AllowsNewStateVersionForSameIdentity()
    {
        var history = new Mock<IHistoryRepository>();
        history
            .Setup(repository => repository.GetByEventTypeAsync(
                "ActivityDismissed",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new History
                {
                    EventType = "ActivityDismissed",
                    SourceTitle = "download-operation:sabnzbd:job-1",
                    Data = "{\"version\":\"importfailed:old error\"}"
                }
            ]);
        history.Setup(repository => repository.AddAsync(
                It.IsAny<History>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((History entry, CancellationToken _) => entry);
        var controller = CreateController(history.Object);

        var result = await controller.DismissActivityEntries(new HistoryController.ActivityDismissalRequest(
        [
            new HistoryController.ActivityDismissalEntry(
                "download-operation:sabnzbd:job-1",
                42,
                "Book",
                "download-1",
                "importfailed:new error")
        ]));

        Assert.IsType<OkObjectResult>(result);
        history.Verify(repository => repository.AddAsync(
            It.Is<History>(entry =>
                entry.SourceTitle == "download-operation:sabnzbd:job-1"
                && entry.Data!.Contains("\"version\":\"importfailed:new error\"", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetVerifiedImportDownloadIds_ReturnsPersistedSuccessfulImportIds()
    {
        var history = new Mock<IHistoryRepository>();
        history.Setup(repository => repository.GetSucceededImportedDownloadIdsAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["download-1"]);
        var controller = CreateController(history.Object);

        var result = Assert.IsType<OkObjectResult>(
            await controller.GetVerifiedImportDownloadIds(CancellationToken.None));

        Assert.Equal(["download-1"], Assert.IsType<List<string>>(result.Value));
    }

    [Fact]
    public async Task GetQuickAddOperations_ReturnsAllUnresolvedDurableOperations()
    {
        var history = new Mock<IHistoryRepository>();
        var operations = new Mock<IQuickAddOperationRepository>();
        operations.Setup(repository => repository.GetUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QuickAddOperation
                {
                    Id = "operation-1",
                    AudiobookId = 42,
                    AudiobookTitle = "Pending Book",
                    State = QuickAddOperationState.SearchPending,
                    CreatedAt = DateTime.UtcNow
                }
            ]);
        var controller = new HistoryController(
            history.Object,
            Mock.Of<IConfigurationService>(),
            NullLogger<HistoryController>.Instance,
            operations.Object);

        var result = Assert.IsType<OkObjectResult>(
            await controller.GetQuickAddOperations(CancellationToken.None));
        var item = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HistoryController.QuickAddOperationActivityItem>>(
            result.Value));
        Assert.Equal("operation-1", item.Id);
        Assert.Equal("SearchPending", item.State);
        Assert.Equal("Pending Book", item.AudiobookTitle);
    }

    [Fact]
    public async Task RetryQuickAddOperation_ReturnsAcceptedAndReusesOperationIdentity()
    {
        var history = new Mock<IHistoryRepository>();
        var operations = new Mock<IQuickAddOperationRepository>();
        operations.Setup(repository => repository.RetryAsync(
                "operation-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = new HistoryController(
            history.Object,
            Mock.Of<IConfigurationService>(),
            NullLogger<HistoryController>.Instance,
            operations.Object);

        var result = Assert.IsType<AcceptedResult>(
            await controller.RetryQuickAddOperation("operation-1", CancellationToken.None));

        Assert.NotNull(result.Value);
        operations.VerifyAll();
    }

    private static HistoryController CreateController(IHistoryRepository history) =>
        new(
            history,
            Mock.Of<IConfigurationService>(),
            NullLogger<HistoryController>.Instance,
            Mock.Of<IQuickAddOperationRepository>());
}
