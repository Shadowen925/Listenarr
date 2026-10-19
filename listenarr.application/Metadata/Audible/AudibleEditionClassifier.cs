using System.Text.RegularExpressions;

namespace Listenarr.Application.Metadata.Audible;

public static partial class AudibleEditionClassifier
{
    [GeneratedRegex(@"\bpart\s*(\d+)\s*(?:of|/)\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

    public static AudiobookProductionType ClassifyProductionType(params string?[] metadata)
    {
        var text = string.Join(' ', metadata.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (ContainsAny(text, "full cast", "full-cast", "ensemble cast"))
        {
            return AudiobookProductionType.FullCast;
        }

        if (ContainsAny(text, "dramatized", "dramatized adaptation", "audio drama", "radio play"))
        {
            return AudiobookProductionType.Dramatized;
        }

        return AudiobookProductionType.Standard;
    }

    public static (int? PartNumber, int? TotalParts) ParseParts(params string?[] metadata)
    {
        foreach (var value in metadata)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var match = PartPattern().Match(value);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out var partNumber)
                && int.TryParse(match.Groups[2].Value, out var totalParts)
                && partNumber > 0
                && totalParts >= partNumber)
            {
                return (partNumber, totalParts);
            }
        }

        return (null, null);
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
