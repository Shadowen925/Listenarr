using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Name", nameof(QualityProfileControllerEditionTests))]
[Trait("Category", "QualityProfile")]
public sealed class QualityProfileControllerEditionTests : Listenarr.Tests.Common.BaseTests
{
    [Fact]
    public async Task ScoreResults_WithAudiobookId_ReturnsEditionMatchEvidence()
    {
        var audiobook = new Audiobook
        {
            Id = 42,
            Title = "Dracula",
            Asin = "B000DRACULA",
            Language = "English"
        };
        var result = new SearchResult
        {
            Id = "release-1",
            Title = "Dracula - Audible Original",
            Asin = "B000DRACULA",
            Language = "English"
        };
        var profile = new QualityProfile { Id = 7, Name = "Default" };
        var score = new QualityScore { SearchResult = result, TotalScore = 80 };
        var qualityProfiles = new Mock<IQualityProfileService>();
        qualityProfiles.Setup(service => service.GetByIdAsync(7)).ReturnsAsync(profile);
        qualityProfiles
            .Setup(service => service.ScoreSearchResults(
                It.Is<List<SearchResult>>(results => results.Count == 1),
                profile))
            .ReturnsAsync([score]);
        var audiobooks = new Mock<IAudiobookRepository>();
        audiobooks.Setup(repository => repository.GetByIdAsync(42)).ReturnsAsync(audiobook);
        var controller = new QualityProfileController(
            qualityProfiles.Object,
            NullLogger<QualityProfileController>.Instance,
            audiobooks.Object);

        var actionResult = await controller.ScoreResults(7, [result], audiobookId: 42);
        var response = Assert.IsType<OkObjectResult>(actionResult.Result);
        var scores = Assert.IsType<List<QualityScore>>(response.Value);

        Assert.True(scores.Single().MatchesAudiobookEdition);
        Assert.Equal(100, scores.Single().EditionMatchConfidence);
        Assert.Contains("AsinMatch", scores.Single().EditionMatchEvidence);
        Assert.Empty(scores.Single().EditionMatchConflicts);
    }
}
