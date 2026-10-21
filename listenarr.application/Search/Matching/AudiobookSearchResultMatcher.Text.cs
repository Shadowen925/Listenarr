using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Matching
{
    public static partial class AudiobookSearchResultMatcher
    {
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
            if (normalizedText.Contains("full cast", StringComparison.Ordinal)
                || normalizedText.Contains("fullcast", StringComparison.Ordinal))
            {
                return AudiobookProductionType.FullCast;
            }

            if (normalizedText.Contains("dramatized", StringComparison.Ordinal)
                || normalizedText.Contains("adaptation", StringComparison.Ordinal)
                || normalizedText.Contains("adapted", StringComparison.Ordinal)
                || normalizedText.Contains("dramatic adaptation", StringComparison.Ordinal)
                || normalizedText.Contains("audio drama", StringComparison.Ordinal)
                || normalizedText.Contains("radio play", StringComparison.Ordinal)
                || normalizedText.Contains("radio drama", StringComparison.Ordinal))
            {
                return AudiobookProductionType.Dramatized;
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
