/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Application.Search.Matching;

namespace Listenarr.Application.Metadata.Goodreads
{
    /// <summary>Selects the Goodreads search hit that is the same work as a library entry.</summary>
    public static partial class GoodreadsCandidateMatcher
    {
        private static readonly HashSet<string> JunkTitleWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "summary", "study", "guide", "analysis", "workbook", "companion", "trivia", "sparknotes", "cliffsnotes"
        };

        public static GoodreadsSearchCandidate? SelectBest(Audiobook audiobook, IEnumerable<GoodreadsSearchCandidate> candidates)
        {
            ArgumentNullException.ThrowIfNull(audiobook);
            return candidates
                .Where(candidate => IsMatch(audiobook, candidate))
                .OrderByDescending(candidate => candidate.RatingsCount ?? 0)
                .FirstOrDefault();
        }

        public static bool IsMatch(Audiobook audiobook, GoodreadsSearchCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(audiobook.Title) || string.IsNullOrWhiteSpace(candidate.Title))
            {
                return false;
            }

            var libraryTitles = TitleKeys(audiobook.Title);
            var candidateTitles = TitleKeys(candidate.Title);
            candidateTitles.UnionWith(TitleKeys(candidate.BareTitle));
            if (!libraryTitles.Overlaps(candidateTitles))
            {
                return false;
            }

            // A study guide or summary shares the title but is a different work.
            var candidateWords = Tokens(candidate.Title);
            var libraryWords = Tokens(audiobook.Title);
            if (candidateWords.Any(word => JunkTitleWords.Contains(word) && !libraryWords.Contains(word)))
            {
                return false;
            }

            var libraryAuthors = audiobook.Authors?.Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? [];
            if (libraryAuthors.Count == 0)
            {
                return true;
            }

            var candidateAuthorTokens = Tokens(candidate.Author);
            if (candidateAuthorTokens.Count == 0)
            {
                return false;
            }

            // Last name must match; first names/initials vary between sources.
            return libraryAuthors.SelectMany(SplitAuthors).Any(author =>
            {
                var tokens = Tokens(author);
                return tokens.Count > 0 && candidateAuthorTokens.Contains(tokens[^1]);
            });
        }

        private static IEnumerable<string> SplitAuthors(string authors) =>
            authors.Split([',', ';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(part => Regex.Split(part, @"\s+and\s+", RegexOptions.IgnoreCase))
                .Where(part => part.Length > 0);

        private static HashSet<string> TitleKeys(string? title)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(title))
            {
                return keys;
            }

            var withoutSeries = SeriesParentheticalRegex().Replace(title, string.Empty);
            var core = AudiobookSearchQueryPlanner.GetCoreTitle(withoutSeries);
            foreach (var variant in new[] { withoutSeries, core, AudiobookSearchQueryPlanner.StripLeadingArticle(core) })
            {
                var key = string.Join(' ', Tokens(variant));
                if (key.Length > 0)
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private static List<string> Tokens(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [];
            }

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }

            return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        [GeneratedRegex(@"\s*\([^)]*#\s*\d+[^)]*\)\s*$", RegexOptions.CultureInvariant)]
        private static partial Regex SeriesParentheticalRegex();
    }
}
