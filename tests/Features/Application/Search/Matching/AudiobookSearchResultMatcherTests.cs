using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching
{
    [Trait("Name", nameof(AudiobookSearchResultMatcherTests))]
    [Trait("Category", "Application")]
    public class AudiobookSearchResultMatcherTests : BaseTests
    {
        [Fact]
        public void MatchesAsin_RejectsAnExplicitDifferentBook()
        {
            var audiobook = new Audiobook { Asin = "B012345678" };

            Assert.False(AudiobookSearchResultMatcher.MatchesAsin(
                audiobook,
                new SearchResult { Asin = "B087654321" }));
        }

        [Fact]
        public void MatchesAsin_AllowsMatchingOrUnspecifiedResultAsin()
        {
            var audiobook = new Audiobook { Asin = "B012345678" };

            Assert.True(AudiobookSearchResultMatcher.MatchesAsin(
                audiobook,
                new SearchResult { Asin = "b012345678" }));
            Assert.True(AudiobookSearchResultMatcher.MatchesAsin(
                audiobook,
                new SearchResult()));
        }

        [Fact]
        public void MatchesIdentity_RejectsDifferentAuthorEvenWhenTitleMatches()
        {
            var audiobook = new Audiobook
            {
                Title = "Breathless",
                Authors = ["Cat Wynn"]
            };

            Assert.False(AudiobookSearchResultMatcher.MatchesIdentity(
                audiobook,
                new SearchResult { Title = "Breathless", Artist = "Alice Smith" }));
        }

        [Fact]
        public void MatchesIdentity_RejectsDifferentTitleWhenNoStrongIdentifierMatches()
        {
            var audiobook = new Audiobook
            {
                Title = "Breathless",
                Authors = ["Cat Wynn"]
            };

            Assert.False(AudiobookSearchResultMatcher.MatchesIdentity(
                audiobook,
                new SearchResult { Title = "Moonlight", Artist = "Cat Wynn" }));
        }

        [Fact]
        public void MatchesIdentity_AcceptsSeriesPositionAndAuthorWhenReleaseUsesSeriesTitle()
        {
            var audiobook = new Audiobook
            {
                Title = "The Hidden World",
                Authors = ["Troy Ashford"],
                Series = "Mystic Seekers",
                SeriesNumber = "1"
            };

            Assert.True(AudiobookSearchResultMatcher.MatchesIdentity(
                audiobook,
                new SearchResult { Title = "Mystic Seekers 1 Troy Ashford", Artist = "Troy Ashford" }));
        }

        [Fact]
        public void MatchesIdentity_RejectsConflictingPartNumbersInReleaseTitle()
        {
            var audiobook = new Audiobook
            {
                Title = "The Hidden World",
                PartNumber = 1,
                TotalParts = 2
            };

            Assert.False(AudiobookSearchResultMatcher.MatchesIdentity(
                audiobook,
                new SearchResult { Asin = "B000000000", Title = "The Hidden World Part 2 of 2" }));
        }

        [Fact]
        public void MatchesIdentity_AllowsMissingMetadataAsUncertain()
        {
            var audiobook = new Audiobook { Title = "Breathless", Authors = ["Cat Wynn"] };

            Assert.True(AudiobookSearchResultMatcher.MatchesIdentity(audiobook, new SearchResult()));
        }
    }
}
