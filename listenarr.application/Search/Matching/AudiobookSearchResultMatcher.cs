using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Matching
{
    public sealed record AudiobookMatchEvidence(
        bool IsAccepted,
        int Confidence,
        IReadOnlyList<string> PositiveEvidence,
        IReadOnlyList<string> Conflicts);

    public static partial class AudiobookSearchResultMatcher
    {
        private static readonly HashSet<string> IgnoredTitleTokens =
            new(StringComparer.OrdinalIgnoreCase) { "a", "an", "the", "s" };

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

            var languageEvidence = EvaluateLanguage(audiobook, result);
            if (languageEvidence.Conflict != null)
            {
                conflicts.Add(languageEvidence.Conflict);
            }
            else if (languageEvidence.Matches)
            {
                positiveEvidence.Add("LanguageMatch");
            }

            var editionMatches = MatchesEditionLabel(audiobook, result);
            if (editionMatches)
            {
                positiveEvidence.Add("EditionMatch");
            }

            var productionMatches = MatchesProductionType(audiobook, result);
            if (productionMatches)
            {
                positiveEvidence.Add("ProductionTypeMatch");
            }

            var abridgementMatches = MatchesAbridgement(audiobook, result);
            if (abridgementMatches)
            {
                positiveEvidence.Add("AbridgementMatch");
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

            var narratorMatches = MatchesNarrator(audiobook, result);
            if (narratorMatches)
            {
                positiveEvidence.Add("NarratorMatch");
            }

            var runtimeMatches = MatchesRuntime(audiobook, result);
            if (runtimeMatches)
            {
                positiveEvidence.Add("RuntimeMatch");
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
            if (!asinMatches)
            {
                confidence = Math.Min(
                    99,
                    confidence
                        + (editionMatches ? 6 : 0)
                        + (productionMatches ? 6 : 0)
                        + (abridgementMatches ? 4 : 0)
                        + (narratorMatches ? 5 : 0)
                        + (runtimeMatches ? 3 : 0)
                        + (languageEvidence.Matches ? 2 : 0));
            }
            return new AudiobookMatchEvidence(accepted, confidence, positiveEvidence, conflicts);
        }

        private static (bool Matches, string? Conflict) EvaluateLanguage(Audiobook audiobook, SearchResult result)
        {
            var title = string.Join(" ", result.Title, result.Subtitle, result.Edition, result.Description);
            if (SearchResultAttributeParser.HasConflictingExplicitLanguages(title))
            {
                return (false, null);
            }

            var titleLanguage = SearchResultAttributeParser.ParseExplicitLanguageFromText(title);
            var metadataLanguage = NormalizeLanguage(result.Language);
            if (titleLanguage != null && metadataLanguage != null
                && !string.Equals(titleLanguage, metadataLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "ConflictingReleaseLanguageMetadata");
            }

            var expectedLanguage = NormalizeLanguage(audiobook.Language);
            var candidateLanguage = titleLanguage ?? metadataLanguage;
            if (expectedLanguage == null || candidateLanguage == null)
            {
                return (false, null);
            }

            return string.Equals(expectedLanguage, candidateLanguage, StringComparison.OrdinalIgnoreCase)
                ? (true, null)
                : (false, "ConflictingLanguage");
        }

        private static string? NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            return SearchResultAttributeParser.ParseLanguageFromCode(language.Trim())
                ?? SearchResultAttributeParser.ParseExplicitLanguageFromText(language)
                ?? language.Trim();
        }

        private static bool MatchesNarrator(Audiobook audiobook, SearchResult result)
        {
            var expectedNarrators = audiobook.Narrators?
                .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                .Select(Tokenize)
                .Where(tokens => tokens.Count > 0)
                .ToList() ?? [];
            if (expectedNarrators.Count == 0)
            {
                return false;
            }

            var candidate = Tokenize(string.Join(" ", result.Narrator, result.Title, result.Description));
            return expectedNarrators.Any(expected => expected.All(candidate.Contains));
        }

        private static bool MatchesEditionLabel(Audiobook audiobook, SearchResult result)
        {
            var candidate = string.Join(
                " ",
                result.Edition,
                result.Version,
                result.Title,
                result.Subtitle,
                result.Description);
            return MatchesEditionLabel(candidate, audiobook.Edition)
                || MatchesEditionLabel(candidate, audiobook.Version);
        }

        private static bool MatchesEditionLabel(string candidate, string? expected)
        {
            return expected != null
                && SignificantTokens(expected).Count > 0
                && ContainsTitle(candidate, expected);
        }

        private static bool MatchesProductionType(Audiobook audiobook, SearchResult result)
        {
            var candidateType = result.ProductionType != AudiobookProductionType.Standard
                ? result.ProductionType
                : GetProductionType(Normalize(string.Join(
                    " ",
                    result.Edition,
                    result.Version,
                    result.Title,
                    result.Subtitle,
                    result.Description)));
            return candidateType.HasValue && candidateType.Value == audiobook.ProductionType;
        }

        private static bool MatchesAbridgement(Audiobook audiobook, SearchResult result)
        {
            var candidateText = Normalize(string.Join(
                " ",
                result.Edition,
                result.Version,
                result.Title,
                result.Description));
            var candidateAbridged = result.Abridged || HasAny(candidateText, "abridged");
            var candidateUnabridged = HasAny(candidateText, "unabridged");
            return audiobook.Abridged
                ? candidateAbridged && !candidateUnabridged
                : candidateUnabridged && !candidateAbridged;
        }

        private static bool MatchesRuntime(Audiobook audiobook, SearchResult result)
        {
            if (audiobook.Runtime is not > 0)
            {
                return false;
            }

            var candidateRuntime = result.Runtime ?? ParseRuntimeMinutes(result.Title, result.Description);
            return candidateRuntime is > 0
                && Math.Abs(candidateRuntime.Value - audiobook.Runtime.Value) <= Math.Max(15, audiobook.Runtime.Value * 0.15);
        }

        private static int? ParseRuntimeMinutes(params string?[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var match = Regex.Match(
                    value,
                    @"\b(?:(\d{1,2})\s*(?:h|hr|hours?)\s*)?(\d{1,3})\s*(?:m|min|minutes?)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    var hours = match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var parsedHours)
                        ? parsedHours
                        : 0;
                    if (int.TryParse(match.Groups[2].Value, out var minutes))
                    {
                        return (hours * 60) + minutes;
                    }
                }
            }

            return null;
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
            // "Assassin's Quest" must also match releases that write "Assassins Quest".
            return ContainsTitleTokens(candidateTitle, expectedTitle, joinApostrophes: false)
                || ContainsTitleTokens(candidateTitle, expectedTitle, joinApostrophes: true);
        }

        private static bool ContainsTitleTokens(string candidateTitle, string expectedTitle, bool joinApostrophes)
        {
            var expectedTokens = Tokenize(expectedTitle, joinApostrophes)
                .Where(token => !IgnoredTitleTokens.Contains(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expectedTokens.Count == 0)
            {
                return false;
            }

            var candidateTokens = Tokenize(candidateTitle, joinApostrophes).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    }
}
