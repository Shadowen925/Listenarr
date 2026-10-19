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
    }
}
