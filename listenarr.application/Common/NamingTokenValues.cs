using System.Globalization;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Common
{
    /// <summary>
    /// Display names (as shown in the token builder) mapped to the variable key that backs them, plus the
    /// derivation of every Chaptarr-style token from the base variables callers already supply.
    /// </summary>
    internal static class NamingTokenValues
    {
        public static readonly IReadOnlyList<(string Display, string Key)> Catalog = BuildCatalog();

        private static readonly Regex LeadingArticle = new(@"^(The|A|An)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NameSplit = new(@"\s*(?:,|;|&|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<(string, string)> BuildCatalog()
        {
            var entries = new List<(string, string)>();
            void Add(string display, string? key = null) => entries.Add((display, key ?? display.Replace(" ", string.Empty)));

            foreach (var name in new[]
            {
                "Author Name", "Author NameFirstLast", "Author NameThe", "Author FirstInitial", "Author LastInitial",
                "Author CleanName", "Author SortName", "Author Disambiguation",
                "Book Title", "Book TitleThe", "Book CleanTitle", "Book TitleNoSub", "Book TitleTheNoSub",
                "Book CleanTitleNoSub", "Book Subtitle", "Book SubtitleThe", "Book CleanSubtitle", "Book Disambiguation",
                "PartNumber", "PartCount", "SeriesPosition", "SeriesTitleNumber", "SeriesFolder", "BookTitle-WithSeries",
                "BookSeries", "Narrator CleanName", "Narrator Initials", "Narrator First", "Narrator Last", "Narrators",
                "Release Year", "Release YearFirst", "Edition Year", "Quality Full", "Quality Title",
                "MediaInfo AudioCodec", "MediaInfo AudioChannels", "MediaInfo AudioBitRate",
                "MediaInfo AudioBitsPerSample", "MediaInfo AudioSampleRate", "GraphicAudio", "AudioProductionType",
                "Release Group", "Custom Formats", "Original Title", "Original Filename",
            })
            {
                Add(name);
            }

            // Chaptarr spellings for the series name/position map onto the Listenarr tokens.
            Add("Book Series", "Series");
            Add("Book SeriesPosition", "SeriesPosition");
            return entries;
        }

        public static string Normalize(string name) => Regex.Replace(name.ToLowerInvariant(), @"[ ._-]", " ");

        /// <summary>Adds every derived token to <paramref name="vars"/> without overriding values callers supplied.</summary>
        public static void AddDerived(IDictionary<string, string> vars)
        {
            string Get(string key) => vars.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
            void Put(string key, string value)
            {
                if (!vars.ContainsKey(key) || string.IsNullOrWhiteSpace(vars[key]))
                {
                    vars[key] = value;
                }
            }

            var author = FirstName(Get("Author"));
            var title = Get("Title");
            var subtitle = Get("Subtitle");
            var series = Get("Series");
            var narratorFull = Get("Narrator");
            var narrator = FirstName(narratorFull);
            var quality = Get("Quality");

            var authorWords = Words(author);
            Put("AuthorName", Get("Author"));
            Put("AuthorNameFirstLast", authorWords.Length > 2 ? $"{authorWords[0]} {authorWords[^1]}" : author);
            Put("AuthorNameThe", MoveArticle(author));
            Put("AuthorFirstInitial", Initial(authorWords.FirstOrDefault()));
            Put("AuthorLastInitial", Initial(authorWords.LastOrDefault()));
            Put("AuthorCleanName", Clean(author));
            Put("AuthorSortName", authorWords.Length > 1 ? $"{authorWords[^1]}, {string.Join(' ', authorWords[..^1])}" : author);
            Put("AuthorDisambiguation", string.Empty);

            string WithSubtitle(string main, string sub) => sub.Length == 0 ? main : $"{main}: {sub}";
            var showSubtitle = subtitle.Length > 0 && title.IndexOf(subtitle, StringComparison.OrdinalIgnoreCase) < 0;
            var sub = showSubtitle ? subtitle : string.Empty;
            Put("BookTitle", WithSubtitle(title, sub));
            Put("BookTitleThe", WithSubtitle(MoveArticle(title), sub));
            Put("BookCleanTitle", WithSubtitle(Clean(title), Clean(sub)));
            Put("BookTitleNoSub", title);
            Put("BookTitleTheNoSub", MoveArticle(title));
            Put("BookCleanTitleNoSub", Clean(title));
            Put("BookSubtitle", sub);
            Put("BookSubtitleThe", sub.Length == 0 ? string.Empty : MoveArticle(sub));
            Put("BookCleanSubtitle", Clean(sub));
            Put("BookDisambiguation", string.Empty);

            var rawPosition = series.Length == 0 ? string.Empty : Get("SeriesNumber");
            var position = decimal.TryParse(rawPosition, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.ToString("0.##", CultureInfo.InvariantCulture)
                : rawPosition;
            Put("SeriesPosition", position);
            Put("SeriesTitleNumber", series.Length == 0 ? string.Empty : position.Length == 0 ? series : $"{series} #{position}");
            Put("SeriesFolder", series);
            Put("BookSeries", series.Length == 0 || position.Length == 0 ? string.Empty : $"Book {position}");
            var seriesBook = series.Length == 0 || position.Length == 0
                ? string.Empty
                : decimal.TryParse(position, NumberStyles.Number, CultureInfo.InvariantCulture, out var seriesPosition)
                    ? $"Book {seriesPosition.ToString("00.##", CultureInfo.InvariantCulture)}"
                    : $"Book {position}";
            Put("SeriesBook", seriesBook);
            var titleWithSeries = Get("BookTitle");
            Put("BookTitle-WithSeries", series.Length == 0
                ? titleWithSeries
                : $"{titleWithSeries} - {vars["SeriesTitleNumber"]}");

            var narratorWords = Words(narrator);
            Put("Narrators", narratorFull);
            Put("NarratorCleanName", Clean(narrator));
            Put("NarratorInitials", string.Concat(narratorWords.Select(Initial)));
            Put("NarratorFirst", narratorWords.FirstOrDefault() ?? string.Empty);
            Put("NarratorLast", narratorWords.Length > 1 ? narratorWords[^1] : string.Empty);

            var year = Get("Year");
            Put("ReleaseYear", year);
            Put("ReleaseYearFirst", year);
            Put("EditionYear", year);

            var isBitRate = quality.EndsWith("kbps", StringComparison.OrdinalIgnoreCase);
            Put("QualityFull", quality);
            Put("QualityTitle", isBitRate ? string.Empty : quality.ToUpperInvariant());
            Put("MediaInfoAudioBitRate", isBitRate ? quality : string.Empty);

            Put("PartNumber", FirstNonEmpty(Get("DiskNumber"), Get("ChapterNumber")));
            Put("PartCount", string.Empty);
            var graphic = Get("Publisher").Replace(" ", string.Empty).Contains("GraphicAudio", StringComparison.OrdinalIgnoreCase);
            Put("GraphicAudio", graphic ? "GraphicAudio" : string.Empty);
            foreach (var empty in new[]
            {
                "AudioProductionType", "MediaInfoAudioCodec", "MediaInfoAudioChannels", "MediaInfoAudioBitsPerSample",
                "MediaInfoAudioSampleRate", "ReleaseGroup", "CustomFormats", "OriginalTitle", "OriginalFilename",
            })
            {
                Put(empty, string.Empty);
            }
        }

        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? string.Empty;

        private static string FirstName(string names) =>
            NameSplit.Split(names).FirstOrDefault(n => n.Length > 0)?.Trim() ?? string.Empty;

        private static string[] Words(string value) =>
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        private static string Initial(string? word) =>
            string.IsNullOrEmpty(word) ? string.Empty : char.ToUpperInvariant(word[0]).ToString();

        private static string Clean(string value) =>
            Regex.Replace(value.Replace("'", string.Empty).Replace("\u2019", string.Empty).Replace("\"", string.Empty), @"\s+", " ").Trim();

        private static string MoveArticle(string value)
        {
            var match = LeadingArticle.Match(value);
            return match.Success ? $"{match.Groups[2].Value}, {match.Groups[1].Value}" : value;
        }
    }
}
