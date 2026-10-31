using Listenarr.Application.Metadata.Goodreads;
using Listenarr.Application.Search.Matching;
using Listenarr.Infrastructure.Metadata.Providers.Goodreads;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching
{
    [Trait("Name", nameof(SearchPlanningAndGoodreadsTests))]
    [Trait("Category", "Application")]
    public class SearchPlanningAndGoodreadsTests : BaseTests
    {
        [Fact]
        public void Planner_ProducesBroadVariantsWithoutDuplicates()
        {
            var book = new Audiobook
            {
                Title = "The Name of the Wind: Kingkiller Chronicle, Book 1",
                Authors = ["Patrick Rothfuss"],
                Asin = "B002UZMLXM",
                Isbn = ["9780756404741"],
                Series = "Kingkiller Chronicle",
                SeriesNumber = "1"
            };

            var queries = AudiobookSearchQueryPlanner.Build(book, 14);

            Assert.Contains("The Name of the Wind Patrick Rothfuss", queries);
            Assert.Contains("The Name of the Wind", queries);
            Assert.Contains("Name of the Wind Patrick Rothfuss", queries);
            Assert.Contains("Name of the Wind", queries);
            Assert.Contains("B002UZMLXM", queries);
            Assert.Contains("9780756404741", queries);
            Assert.Contains("Kingkiller Chronicle 1 Patrick Rothfuss", queries);
            Assert.Contains("Patrick Rothfuss", queries);
            Assert.Equal(queries.Count, queries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void Matcher_AcceptsRealBookAndRejectsStudyGuideAndWrongAuthor()
        {
            var book = new Audiobook { Title = "Royal Assassin", Authors = ["Robin Hobb"] };

            var best = GoodreadsCandidateMatcher.SelectBest(book,
            [
                new GoodreadsSearchCandidate("1", "Royal Assassin Study Guide", null, "SparkNotes", 900000),
                new GoodreadsSearchCandidate("2", "Royal Assassin", "Royal Assassin", "Someone Else", 800000),
                new GoodreadsSearchCandidate("3", "Royal Assassin (Farseer Trilogy, #2)", "Royal Assassin", "Robin Hobb", 150000)
            ]);

            Assert.Equal("3", best?.BookId);
        }

        [Fact]
        public void Parser_ReadsAutocompleteAndBookPage()
        {
            var candidates = GoodreadsClient.ParseSearch(
                "[{\"bookId\":\"77\",\"title\":\"Royal Assassin\",\"bookTitleBare\":\"Royal Assassin\",\"ratingsCount\":\"1200\",\"author\":{\"name\":\"Robin Hobb\"}}]");
            Assert.Equal("77", Assert.Single(candidates).BookId);
            Assert.Equal(1200, candidates[0].RatingsCount);

            const string html = "<html><script id=\"__NEXT_DATA__\" type=\"application/json\">" +
                "{\"props\":{\"pageProps\":{\"apolloState\":{" +
                "\"Book:a\":{\"legacyId\":77,\"title\":\"Royal Assassin\",\"description({\\\"stripped\\\":true})\":\"Fitz grows up.\"," +
                "\"primaryContributorEdge\":{\"node\":{\"__ref\":\"Contributor:1\"}}," +
                "\"bookSeries\":[{\"userPosition\":\"2\",\"series\":{\"__ref\":\"Series:1\"}}]," +
                "\"bookGenres\":[{\"genre\":{\"name\":\"Fantasy\"}}]," +
                "\"details\":{\"isbn13\":\"9780553565690\",\"publisher\":\"Spectra\",\"publicationTime\":1000000000000,\"language\":{\"name\":\"English\"}}}," +
                "\"Book:b\":{\"legacyId\":99,\"title\":\"Other\"}," +
                "\"Contributor:1\":{\"name\":\"Robin Hobb\"},\"Series:1\":{\"title\":\"Farseer Trilogy\"}}}}}</script></html>";

            var details = GoodreadsClient.ParseBookPage(html, "77");

            Assert.NotNull(details);
            Assert.Equal("Robin Hobb", details!.Author);
            Assert.Equal("Fitz grows up.", details.Description);
            Assert.Equal(new GoodreadsSeriesInfo("Farseer Trilogy", "2"), Assert.Single(details.Series));
            Assert.Equal("Fantasy", Assert.Single(details.Genres));
            Assert.Equal("9780553565690", details.Isbn13);
            Assert.Equal(2001, details.PublishedUtc?.Year);
            Assert.Null(GoodreadsClient.ParseBookPage(html, "12345"));
        }

        [Fact]
        public void Merge_FillsMissingFieldsWithoutOverwritingExistingOnes()
        {
            var book = new Audiobook
            {
                Title = "Royal Assassin",
                Description = "Mine",
                Asin = "B003NTPCVM",
                Genres = ["Fantasy"]
            };

            GoodreadsEnrichmentService.Merge(book, new GoodreadsBookDetails
            {
                Description = "Theirs",
                Genres = ["fantasy", "Epic"],
                Isbn13 = "9780553565690",
                Publisher = "Spectra",
                Series = [new GoodreadsSeriesInfo("Farseer Trilogy", "2")]
            });

            Assert.Equal("Mine", book.Description);
            Assert.Equal("B003NTPCVM", book.Asin);
            Assert.Equal(["Fantasy", "Epic"], book.Genres);
            Assert.Equal(["9780553565690"], book.Isbn);
            Assert.Equal("Spectra", book.Publisher);
            Assert.Equal("Farseer Trilogy", book.Series);
        }
    }
}
