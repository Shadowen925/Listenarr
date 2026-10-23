using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Category", "FileNamingService")]
    [Trait("Name", "FileNamingService_TokenTests")]
    public class FileNamingService_TokenTests : BaseTests
    {
        private readonly FileNamingService _service = new(
            new Mock<IConfigurationService>().Object,
            NullLogger<FileNamingService>.Instance);

        private static AudioMetadata Gunslinger(bool series = true) => new()
        {
            Title = "The Gunslinger",
            Artist = "Stephen King",
            Narrator = "George Guidall",
            Series = series ? "The Dark Tower" : null,
            SeriesPosition = series ? 2 : null,
        };

        private string Apply(string pattern, AudioMetadata metadata) =>
            _service.ApplyNamingPattern(pattern, metadata).Replace(Path.DirectorySeparatorChar, '/');

        private const string UserPattern =
            "{Author Name}{ - Series}{ - Narrator}/{Series}{ - SeriesPosition:00} - {Book Title}";

        [Fact]
        public void OptionalGroupsRenderWhenSeriesIsPresent() =>
            Assert.Equal(
                "Stephen King - The Dark Tower - George Guidall/The Dark Tower - 02 - The Gunslinger",
                Apply(UserPattern, Gunslinger()));

        [Fact]
        public void OptionalGroupsDisappearWithoutSeries() =>
            Assert.Equal(
                "Stephen King - George Guidall/The Gunslinger",
                Apply(UserPattern, Gunslinger(series: false)));

        [Fact]
        public void BookSeriesRendersBookLabelWithOptionalPadding()
        {
            Assert.Equal("Book 2 - The Gunslinger", Apply("{BookSeries} - {Book Title}", Gunslinger()));
            Assert.Equal("Book 02 - The Gunslinger", Apply("{BookSeries:00} - {Book Title}", Gunslinger()));
            Assert.Equal("The Gunslinger", Apply("{BookSeries} - {Book Title}", Gunslinger(series: false)));
        }

        [Fact]
        public void LegacySeriesBookTokenIsUnchanged() =>
            Assert.Equal("Book 02 - The Gunslinger", Apply("{SeriesBook} - {Title}", Gunslinger()));

        [Fact]
        public void SeparatorAndCaseFollowTheTokenSpelling()
        {
            Assert.Equal("Stephen.King.The.Gunslinger", Apply("{Author.Name}.{Book.Title}", Gunslinger()));
            Assert.Equal("stephen king", Apply("{author name}", Gunslinger()));
            Assert.Equal("STEPHEN KING", Apply("{AUTHOR NAME}", Gunslinger()));
        }

        [Fact]
        public void NameAndTitleVariantsAreDerived()
        {
            var metadata = Gunslinger();
            Assert.Equal("King, Stephen", Apply("{Author SortName}", metadata));
            Assert.Equal("S", Apply("{Author FirstInitial}", metadata));
            Assert.Equal("GG", Apply("{Narrator Initials}", metadata));
            Assert.Equal("Gunslinger, The", Apply("{Book TitleThe}", metadata));
            Assert.Equal("The Dark Tower #2", Apply("{SeriesTitleNumber}", metadata));
        }

        [Fact]
        public void PartTokensSupportSmartPaddingAndDropWhenCountIsMissing()
        {
            var variables = new Dictionary<string, object>
            {
                ["Title"] = "Book",
                ["DiskNumber"] = 2,
                ["PartCount"] = 10,
            };

            Assert.Equal("Book (02-10)", _service.ApplyNamingPattern("{Title}{ (PartNumber:smart/PartCount)}", variables));
            Assert.Equal("Book", _service.ApplyNamingPattern(
                "{Title}{ (PartNumber/PartCount)}",
                new Dictionary<string, object> { ["Title"] = "Book", ["DiskNumber"] = 2 }));
        }

        [Fact]
        public void LegacyPatternsKeepSeparatorCleanup() =>
            Assert.Equal(
                "Stephen King/The Gunslinger",
                Apply("{Author}/{Series}/{Title}", Gunslinger(series: false)));
    }
}
