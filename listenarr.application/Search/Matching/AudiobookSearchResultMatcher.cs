using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Matching
{
    public static class AudiobookSearchResultMatcher
    {
        private static readonly HashSet<string> IgnoredTitleTokens =
            new(StringComparer.OrdinalIgnoreCase) { "a", "an", "the" };

        public static bool MatchesAsin(Audiobook audiobook, SearchResult result) =>
            string.IsNullOrWhiteSpace(audiobook.Asin)
            || string.IsNullOrWhiteSpace(result.Asin)
            || string.Equals(audiobook.Asin.Trim(), result.Asin.Trim(), StringComparison.OrdinalIgnoreCase);

        public static bool MatchesIdentity(Audiobook audiobook, SearchResult result)
        {
            ArgumentNullException.ThrowIfNull(audiobook);
            ArgumentNullException.ThrowIfNull(result);

            if (!MatchesAsin(audiobook, result) || HasEditionConflict(audiobook, result))
            {
                return false;
            }

            if (HasExplicitAuthorMismatch(audiobook, result))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(audiobook.Asin)
                && !string.IsNullOrWhiteSpace(result.Asin))
            {
                return true;
            }

            var titleMatches = ContainsTitle(result.Title, audiobook.Title ?? string.Empty);
            var seriesPositionMatches = MatchesSeriesPosition(audiobook, result);
            if (titleMatches || seriesPositionMatches)
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(result.Title)
                && string.IsNullOrWhiteSpace(result.Series)
                && string.IsNullOrWhiteSpace(result.Artist);
        }

        private static bool HasExplicitAuthorMismatch(Audiobook audiobook, SearchResult result)
        {
            var expectedAuthors = audiobook.Authors?
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Select(Tokenize)
                .Where(tokens => tokens.Count > 0)
                .ToList() ?? [];
            var actualAuthorTokens = Tokenize(result.Artist);
            if (expectedAuthors.Count == 0 || actualAuthorTokens.Count == 0)
            {
                return false;
            }

            return !expectedAuthors.Any(expected => expected.All(actualAuthorTokens.Contains));
        }

        private static bool HasEditionConflict(Audiobook audiobook, SearchResult result)
        {
            if (result.ProductionType != AudiobookProductionType.Standard
                && result.ProductionType != audiobook.ProductionType)
            {
                return true;
            }

            if (result.Abridged && !audiobook.Abridged)
            {
                return true;
            }

            var parsedPart = ParsePartNumbers(result.Edition, result.Title, result.Description);
            var candidatePartNumber = result.PartNumber ?? parsedPart.PartNumber;
            var candidateTotalParts = result.TotalParts ?? parsedPart.TotalParts;
            if (audiobook.PartNumber.HasValue
                && candidatePartNumber.HasValue
                && audiobook.PartNumber != candidatePartNumber)
            {
                return true;
            }

            if (audiobook.TotalParts.HasValue
                && candidateTotalParts.HasValue
                && audiobook.TotalParts != candidateTotalParts)
            {
                return true;
            }

            var candidateEdition = Normalize(result.Edition);
            var expectedEdition = Normalize(audiobook.Edition);
            var candidateText = Normalize(string.Join(
                " ",
                result.Edition,
                result.Title,
                result.Description,
                result.Subtitle,
                result.Version));

            if (HasAny(candidateText, "abridged") && HasAny(expectedEdition, "unabridged"))
            {
                return true;
            }

            if (HasAny(candidateText, "unabridged") && HasAny(expectedEdition, "abridged"))
            {
                return true;
            }

            if (audiobook.Abridged && HasAny(candidateText, "unabridged"))
            {
                return true;
            }

            var candidateProduction = GetProductionType(candidateText);
            return candidateProduction.HasValue && candidateProduction.Value != audiobook.ProductionType;
        }

        private static bool ContainsTitle(string candidateTitle, string expectedTitle)
        {
            var expectedTokens = Tokenize(expectedTitle)
                .Where(token => !IgnoredTitleTokens.Contains(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expectedTokens.Count == 0)
            {
                return false;
            }

            var candidateTokens = Tokenize(candidateTitle).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return expectedTokens.IsSubsetOf(candidateTokens);
        }

        private static bool MatchesSeriesPosition(Audiobook audiobook, SearchResult result)
        {
            var expectedSeries = audiobook.Series
                ?? audiobook.SeriesMemberships?.FirstOrDefault(membership => membership.IsPrimary)?.SeriesName
                ?? audiobook.SeriesMemberships?.FirstOrDefault()?.SeriesName;
            var expectedNumber = audiobook.SeriesNumber
                ?? audiobook.SeriesMemberships?.FirstOrDefault(membership => membership.IsPrimary)?.SeriesNumber
                ?? audiobook.SeriesMemberships?.FirstOrDefault()?.SeriesNumber;
            var candidateSeries = result.Series
                ?? result.SeriesMemberships?.FirstOrDefault(membership => membership.IsPrimary)?.SeriesName
                ?? result.SeriesMemberships?.FirstOrDefault()?.SeriesName;
            var candidateNumber = result.SeriesNumber
                ?? result.SeriesMemberships?.FirstOrDefault(membership => membership.IsPrimary)?.SeriesNumber
                ?? result.SeriesMemberships?.FirstOrDefault()?.SeriesNumber;

            if (string.IsNullOrWhiteSpace(expectedSeries)
                || string.IsNullOrWhiteSpace(expectedNumber))
            {
                return false;
            }

            var expectedSeriesTokens = Tokenize(expectedSeries)
                .Where(token => !IgnoredTitleTokens.Contains(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expectedSeriesTokens.Count == 0)
            {
                return false;
            }

            var candidateSeriesTokens = Tokenize(candidateSeries);
            var candidateTitleTokens = Tokenize(result.Title);
            var hasSeries = expectedSeriesTokens.IsSubsetOf(candidateSeriesTokens)
                || expectedSeriesTokens.IsSubsetOf(candidateTitleTokens.ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (!hasSeries)
            {
                return false;
            }

            var expectedPosition = Tokenize(expectedNumber);
            var candidatePosition = Tokenize(candidateNumber);
            if (candidatePosition.Count == 0)
            {
                candidatePosition = candidateTitleTokens;
            }

            return expectedPosition.Count > 0 && expectedPosition.All(candidatePosition.Contains);
        }

        private static List<string> Tokenize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [];
            }

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var normalized = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    normalized.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
                }
            }

            return normalized.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static string Normalize(string? value) =>
            string.Join(' ', Tokenize(value));

        private static bool HasAny(string value, params string[] words) =>
            words.Any(word => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(word, StringComparer.OrdinalIgnoreCase));

        private static AudiobookProductionType? GetProductionType(string normalizedText)
        {
            if (normalizedText.Contains("dramatized", StringComparison.Ordinal)
                || normalizedText.Contains("dramatic adaptation", StringComparison.Ordinal))
            {
                return AudiobookProductionType.Dramatized;
            }

            if (normalizedText.Contains("full cast", StringComparison.Ordinal)
                || normalizedText.Contains("fullcast", StringComparison.Ordinal))
            {
                return AudiobookProductionType.FullCast;
            }

            return null;
        }

        private static (int? PartNumber, int? TotalParts) ParsePartNumbers(params string?[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var match = Regex.Match(
                    value,
                    @"\b(?:part|pt)\s*(\d+)\s*(?:of|/)\s*(\d+)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success
                    && int.TryParse(match.Groups[1].Value, out var partNumber)
                    && int.TryParse(match.Groups[2].Value, out var totalParts))
                {
                    return (partNumber, totalParts);
                }

                match = Regex.Match(value, @"\b(?:part|pt)\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups[1].Value, out partNumber))
                {
                    return (partNumber, null);
                }
            }

            return (null, null);
        }
    }
}
