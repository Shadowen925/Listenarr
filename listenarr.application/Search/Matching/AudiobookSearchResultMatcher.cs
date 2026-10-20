using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Matching
{
    public sealed record AudiobookMatchEvidence(
        bool IsAccepted,
        int Confidence,
        IReadOnlyList<string> PositiveEvidence,
        IReadOnlyList<string> Conflicts);

    public static class AudiobookSearchResultMatcher
    {
        private static readonly HashSet<string> IgnoredTitleTokens =
            new(StringComparer.OrdinalIgnoreCase) { "a", "an", "the" };

        public static bool MatchesAsin(Audiobook audiobook, SearchResult result) =>
            string.IsNullOrWhiteSpace(audiobook.Asin)
            || string.IsNullOrWhiteSpace(result.Asin)
            || string.Equals(audiobook.Asin.Trim(), result.Asin.Trim(), StringComparison.OrdinalIgnoreCase);

        public static bool MatchesIdentity(Audiobook audiobook, SearchResult result) =>
            EvaluateIdentity(audiobook, result).IsAccepted;

        public static AudiobookMatchEvidence EvaluateIdentity(Audiobook audiobook, SearchResult result)
        {
            ArgumentNullException.ThrowIfNull(audiobook);
            ArgumentNullException.ThrowIfNull(result);

            var positiveEvidence = new List<string>();
            var conflicts = new List<string>();
            if (!MatchesAsin(audiobook, result))
            {
                conflicts.Add("ConflictingAsin");
            }
            if (HasEditionConflict(audiobook, result))
            {
                conflicts.Add("IncompatibleEdition");
            }

            var titleMatches = MatchesTitle(audiobook, result);
            if (titleMatches)
            {
                positiveEvidence.Add("TitleMatch");
            }

            var (seriesPositionMatches, wrongSeriesPosition) = EvaluateSeriesPosition(audiobook, result);
            if (wrongSeriesPosition)
            {
                conflicts.Add("ConflictingSeriesPosition");
            }
            else if (seriesPositionMatches)
            {
                positiveEvidence.Add("SeriesPositionMatch");
            }

            var asinMatches = !string.IsNullOrWhiteSpace(audiobook.Asin)
                && !string.IsNullOrWhiteSpace(result.Asin)
                && string.Equals(audiobook.Asin.Trim(), result.Asin.Trim(), StringComparison.OrdinalIgnoreCase);
            if (asinMatches)
            {
                positiveEvidence.Add("AsinMatch");
            }

            var (authorMatches, authorConflict) = EvaluateAuthors(audiobook, result);
            if (authorMatches)
            {
                positiveEvidence.Add("AuthorMatch");
            }
            else if (authorConflict)
            {
                conflicts.Add("ConflictingAuthor");
            }

            if (conflicts.Count > 0)
            {
                return new AudiobookMatchEvidence(false, 0, positiveEvidence, conflicts);
            }

            var accepted = titleMatches || seriesPositionMatches || asinMatches;
            if (!accepted)
            {
                conflicts.Add("InsufficientIdentityEvidence");
            }

            var confidence = asinMatches ? 100
                : seriesPositionMatches && authorMatches ? 95
                : titleMatches && authorMatches ? 90
                : seriesPositionMatches ? 85
                : titleMatches ? 75
                : 0;
            return new AudiobookMatchEvidence(accepted, confidence, positiveEvidence, conflicts);
        }

        private static bool MatchesTitle(Audiobook audiobook, SearchResult result)
        {
            var candidate = string.Join(" ", result.Title, result.Subtitle, result.Album, result.Series);
            return GetTitleVariants(audiobook.Title)
                .Concat(GetTitleVariants(audiobook.Subtitle))
                .Any(title => ContainsTitle(candidate, title));
        }

        private static (bool Matches, bool Conflicts) EvaluateAuthors(Audiobook audiobook, SearchResult result)
        {
            var expectedAuthors = audiobook.Authors?
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Select(Tokenize)
                .Where(tokens => tokens.Count > 0)
                .ToList() ?? [];
            if (expectedAuthors.Count == 0)
            {
                return (false, false);
            }

            var candidateTokens = Tokenize(string.Join(
                " ",
                result.Artist,
                result.Title,
                result.Album,
                result.Description,
                result.Publisher));
            if (expectedAuthors.Any(expected => expected.All(candidateTokens.Contains)))
            {
                return (true, false);
            }

            return (false, Tokenize(result.Artist).Count > 0);
        }

        private static bool HasEditionConflict(Audiobook audiobook, SearchResult result)
        {
            if (result.ProductionType != AudiobookProductionType.Standard
                && result.ProductionType != audiobook.ProductionType)
            {
                return true;
            }

            if ((result.Abridged
                    || HasAny(Normalize(string.Join(" ", result.Edition, result.Title, result.Description)), "abridged"))
                && !audiobook.Abridged)
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

        private static (bool Matches, bool Conflicts) EvaluateSeriesPosition(Audiobook audiobook, SearchResult result)
        {
            var expectedSeries = (audiobook.SeriesMemberships ?? [])
                .Where(membership => !string.IsNullOrWhiteSpace(membership.SeriesName)
                    && !string.IsNullOrWhiteSpace(membership.SeriesNumber))
                .Select(membership => (membership.SeriesName!, membership.SeriesNumber!))
                .ToList();
            if (!string.IsNullOrWhiteSpace(audiobook.Series)
                && !string.IsNullOrWhiteSpace(audiobook.SeriesNumber))
            {
                expectedSeries.Insert(0, (audiobook.Series, audiobook.SeriesNumber));
            }

            var title = string.Join(" ", result.Title, result.Subtitle, result.Album);
            var candidateMemberships = (result.SeriesMemberships ?? [])
                .Where(membership => !string.IsNullOrWhiteSpace(membership.SeriesName))
                .Select(membership => (membership.SeriesName!, membership.SeriesNumber))
                .ToList();
            if (!string.IsNullOrWhiteSpace(result.Series))
            {
                candidateMemberships.Insert(0, (result.Series, result.SeriesNumber));
            }

            foreach (var (seriesName, seriesNumber) in expectedSeries.Distinct())
            {
                var seriesTokens = SignificantTokens(seriesName);
                if (seriesTokens.Count == 0)
                {
                    continue;
                }

                var hasSeries = candidateMemberships.Any(candidate =>
                        seriesTokens.SetEquals(SignificantTokens(candidate.Item1)))
                    || ContainsTokenSequence(title, seriesTokens);
                if (!hasSeries)
                {
                    continue;
                }

                var expectedPosition = ParsePosition(seriesNumber);
                if (!expectedPosition.HasValue)
                {
                    continue;
                }

                var candidatePosition = candidateMemberships
                    .Where(candidate => seriesTokens.SetEquals(SignificantTokens(candidate.Item1)))
                    .Select(candidate => ParsePosition(candidate.Item2))
                    .FirstOrDefault(position => position.HasValue);
                candidatePosition ??= ParsePositionAfterSeries(title, seriesName);
                if (candidatePosition.HasValue)
                {
                    return (
                        candidatePosition.Value == expectedPosition.Value,
                        candidatePosition.Value != expectedPosition.Value);
                }
            }

            return (false, false);
        }

        private static IEnumerable<string> GetTitleVariants(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            var trimmed = title.Trim();
            yield return trimmed;
            var colon = trimmed.IndexOf(':');
            if (colon > 0)
            {
                var core = trimmed[..colon].Trim();
                if (!string.IsNullOrWhiteSpace(core))
                {
                    yield return core;
                }
            }
        }

        private static HashSet<string> SignificantTokens(string? value) =>
            Tokenize(value)
                .Where(token => !IgnoredTitleTokens.Contains(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static bool ContainsTokenSequence(string candidate, IReadOnlyCollection<string> expectedTokens)
        {
            var candidateTokens = Tokenize(candidate);
            var expected = expectedTokens.ToArray();
            for (var start = 0; expected.Length > 0 && start <= candidateTokens.Count - expected.Length; start++)
            {
                if (expected.Select((token, index) =>
                        string.Equals(token, candidateTokens[start + index], StringComparison.OrdinalIgnoreCase))
                    .All(matches => matches))
                {
                    return true;
                }
            }

            return false;
        }

        private static decimal? ParsePosition(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = Regex.Match(value, @"\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
            return match.Success && decimal.TryParse(
                match.Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var position)
                ? position
                : null;
        }

        private static decimal? ParsePositionAfterSeries(string title, string series)
        {
            var tokens = Tokenize(series);
            if (tokens.Count == 0)
            {
                return null;
            }

            var seriesPattern = string.Join(@"[\s\p{P}]+", tokens.Select(Regex.Escape));
            var match = Regex.Match(
                title,
                $@"\b{seriesPattern}\b[\s\p{{P}}]*(?:book\s*|#\s*)?(\d+(?:\.\d+)?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? ParsePosition(match.Groups[1].Value) : null;
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
