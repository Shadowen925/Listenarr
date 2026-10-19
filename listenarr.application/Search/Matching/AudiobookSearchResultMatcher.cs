namespace Listenarr.Application.Search.Matching
{
    public static class AudiobookSearchResultMatcher
    {
        public static bool MatchesAsin(Audiobook audiobook, SearchResult result) =>
            string.IsNullOrWhiteSpace(audiobook.Asin)
            || string.IsNullOrWhiteSpace(result.Asin)
            || string.Equals(audiobook.Asin.Trim(), result.Asin.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
