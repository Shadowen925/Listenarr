using System.Security.Cryptography;
using System.Globalization;
using System.Text;
namespace Listenarr.Application.Audiobooks.Common;

public static class AudiobookSeriesIdentity
{
    public static string GetId(
        Audiobook audiobook,
        string seriesName,
        string? seriesAsin = null,
        string? audiobookshelfSeriesId = null)
    {
        if (!string.IsNullOrWhiteSpace(seriesAsin))
        {
            return seriesAsin.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(audiobookshelfSeriesId))
        {
            return $"abs:{audiobookshelfSeriesId.Trim()}";
        }

        var authorContext = string.Join(
            "|",
            (audiobook.Authors ?? [])
                .Select(Normalize)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        var identity = $"{Normalize(seriesName)}\n{authorContext}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"local:{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    public static string NormalizeName(string value) => Normalize(value);

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
            else if (normalized.Length > 0 && normalized[^1] != ' ')
            {
                normalized.Append(' ');
            }
        }

        return normalized.ToString().Trim();
    }
}
