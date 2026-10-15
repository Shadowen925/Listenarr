using System.Globalization;

namespace Listenarr.Application.Audiobooks.Renaming
{
    public partial class RenameService
    {
        private static Dictionary<string, object> BuildNamingVariables(
            Audiobook audiobook,
            string? folderPattern,
            string? filePattern,
            int sequenceNumber,
            bool isMultiFile)
        {
            var usesSubtitleToken =
                (!string.IsNullOrWhiteSpace(folderPattern)
                    && folderPattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(filePattern)
                    && filePattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0);
            var combinedTitle =
                !usesSubtitleToken
                && !string.IsNullOrWhiteSpace(audiobook.Subtitle)
                && !string.IsNullOrWhiteSpace(audiobook.Title)
                && !audiobook.Title.Contains(audiobook.Subtitle, StringComparison.OrdinalIgnoreCase)
                    ? $"{audiobook.Title}: {audiobook.Subtitle}"
                    : audiobook.Title;
            var narrator = audiobook.Narrators != null
                ? string.Join(", ", audiobook.Narrators.Where(
                    name => !string.IsNullOrWhiteSpace(name)))
                : string.Empty;

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "Author", audiobook.Authors?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown Author" },
                { "Series", audiobook.Series ?? string.Empty },
                { "SeriesBook", GetSeriesBookLabel(audiobook.Series, audiobook.SeriesNumber) },
                { "Title", string.IsNullOrWhiteSpace(combinedTitle) ? "Unknown Title" : combinedTitle },
                { "Subtitle", audiobook.Subtitle ?? string.Empty },
                { "Edition", audiobook.Edition ?? string.Empty },
                { "Narrator", narrator },
                { "Publisher", audiobook.Publisher ?? string.Empty },
                { "Language", audiobook.Language ?? string.Empty },
                { "Asin", audiobook.Asin ?? string.Empty },
                { "SeriesNumber", audiobook.SeriesNumber ?? string.Empty },
                { "Year", audiobook.PublishYear ?? string.Empty },
                { "Quality", audiobook.Quality ?? string.Empty },
                { "DiskNumber", isMultiFile ? sequenceNumber : string.Empty },
                { "ChapterNumber", isMultiFile ? sequenceNumber : string.Empty }
            };
        }

        private static string GetSeriesBookLabel(string? series, string? seriesNumber)
        {
            if (string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(seriesNumber))
            {
                return string.Empty;
            }

            return decimal.TryParse(
                seriesNumber,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var position)
                ? $"Book {position.ToString("00.##", CultureInfo.InvariantCulture)}"
                : $"Book {seriesNumber.Trim()}";
        }
    }
}
