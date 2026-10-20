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
        public void EvaluateIdentity_AcceptsHeartOfRadianceSeriesPositionReleaseWithCoauthor()
        {
            var audiobook = new Audiobook
            {
                Title = "Heart of Radiance: A LitRPG Progression Fantasy",
                Authors = ["Luke Chmilenko"],
                Series = "Aetherforged",
                SeriesNumber = "3",
                Asin = "B0H493MK1M"
            };

            var evidence = AudiobookSearchResultMatcher.EvaluateIdentity(audiobook, new SearchResult
            {
                Title = "Aetherforged, Book 3 - Heart of Radiance - Luke Chmilenko, Harmon Cooper [M4B] [128 Kbps]",
                Artist = "Harmon Cooper"
            });

            Assert.True(evidence.IsAccepted);
            Assert.Contains("TitleMatch", evidence.PositiveEvidence);
            Assert.Contains("AuthorMatch", evidence.PositiveEvidence);
            Assert.Contains("SeriesPositionMatch", evidence.PositiveEvidence);
            Assert.Empty(evidence.Conflicts);
        }

        [Theory]
        [InlineData("Aetherforged 3 - Heart of Radiance - Luke Chmilenko")]
        [InlineData("Aetherforged Book 3 - Heart of Radiance - Luke Chmilenko")]
        [InlineData("Aetherforged, Book 3 - Heart of Radiance - Luke Chmilenko")]
        public void MatchesIdentity_AcceptsSeriesNameFirstWithSupportedPositionFormats(string title)
        {
            var audiobook = new Audiobook
            {
                Title = "Heart of Radiance: A LitRPG Progression Fantasy",
                Authors = ["Luke Chmilenko"],
                Series = "Aetherforged",
                SeriesNumber = "3"
            };

            Assert.True(AudiobookSearchResultMatcher.MatchesIdentity(
                audiobook,
                new SearchResult { Title = title }));
        }

        [Fact]
        public void MatchesIdentity_AllowsMarketingSubtitleToBeAbsent()
        {
            var audiobook = new Audiobook
            {
                Title = "Heart of Radiance: A LitRPG Progression Fantasy",
                Subtitle = "A LitRPG Progression Fantasy",
                Authors = ["Luke Chmilenko"]
            };

            Assert.True(AudiobookSearchResultMatcher.MatchesIdentity(
                audiobook,
                new SearchResult { Title = "Heart of Radiance - Luke Chmilenko" }));
        }

        [Fact]
        public void EvaluateIdentity_RejectsWrongSeriesPositionEvenWhenBookTitleMatches()
        {
            var audiobook = new Audiobook
            {
                Title = "Heart of Radiance",
                Series = "Aetherforged",
                SeriesNumber = "3"
            };

            var evidence = AudiobookSearchResultMatcher.EvaluateIdentity(audiobook, new SearchResult
            {
                Title = "Aetherforged Book 2 - Heart of Radiance"
            });

            Assert.False(evidence.IsAccepted);
            Assert.Contains("ConflictingSeriesPosition", evidence.Conflicts);
        }

        [Fact]
        public void EvaluateIdentity_RejectsConflictingAsinAndEdition()
        {
            var audiobook = new Audiobook
            {
                Title = "Heart of Radiance",
                Asin = "B0H493MK1M",
                ProductionType = AudiobookProductionType.Standard
            };

            var evidence = AudiobookSearchResultMatcher.EvaluateIdentity(audiobook, new SearchResult
            {
                Title = "Heart of Radiance - Dramatized Full Cast",
                Asin = "B0WRONG0000",
                ProductionType = AudiobookProductionType.Dramatized
            });

            Assert.False(evidence.IsAccepted);
            Assert.Contains("ConflictingAsin", evidence.Conflicts);
            Assert.Contains("IncompatibleEdition", evidence.Conflicts);
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
        public void MatchesIdentity_RejectsMissingIdentityEvidenceForAutomaticDownload()
        {
            var audiobook = new Audiobook { Title = "Breathless", Authors = ["Cat Wynn"] };

            Assert.False(AudiobookSearchResultMatcher.MatchesIdentity(audiobook, new SearchResult()));
        }
    }
}
