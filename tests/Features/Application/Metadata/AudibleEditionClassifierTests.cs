using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Metadata;

[Trait("Name", nameof(AudibleEditionClassifierTests))]
[Trait("Category", "Application")]
public sealed class AudibleEditionClassifierTests : BaseTests
{
    [Theory]
    [InlineData("Full-cast edition", AudiobookProductionType.FullCast)]
    [InlineData("Audio drama", AudiobookProductionType.Dramatized)]
    [InlineData("Unabridged audiobook", AudiobookProductionType.Standard)]
    public void ClassifyProductionType_UsesAudibleMetadata(
        string metadata,
        AudiobookProductionType expected)
    {
        Assert.Equal(expected, AudibleEditionClassifier.ClassifyProductionType(metadata));
    }

    [Theory]
    [InlineData("Part 1 of 2", 1, 2)]
    [InlineData("Part 3/3", 3, 3)]
    [InlineData("Part 0 of 2", null, null)]
    [InlineData("Part 3 of 2", null, null)]
    public void ParseParts_RequiresValidPartAndTotal(
        string metadata,
        int? expectedPart,
        int? expectedTotal)
    {
        var parts = AudibleEditionClassifier.ParseParts(metadata);

        Assert.Equal(expectedPart, parts.PartNumber);
        Assert.Equal(expectedTotal, parts.TotalParts);
    }
}
