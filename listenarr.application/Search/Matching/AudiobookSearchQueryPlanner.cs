/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Matching
{
    /// <summary>
    /// Expands a library entry into several broad-to-narrow indexer queries (full title, core title,
    /// title without leading articles, author, series position, identifiers). Candidates are narrowed
    /// afterwards by <see cref="AudiobookSearchResultMatcher"/> using the known library metadata.
    /// </summary>
    public static partial class AudiobookSearchQueryPlanner
    {
        public const int DefaultMaxQueries = 10;

        public static IReadOnlyList<string> Build(Audiobook audiobook, int maxQueries = DefaultMaxQueries)
        {
            ArgumentNullException.ThrowIfNull(audiobook);

            var title = Clean(audiobook.Title);
            var subtitle = Clean(audiobook.Subtitle);
            var coreTitle = GetCoreTitle(title);
            var bareTitle = StripLeadingArticle(coreTitle);
            var author = Clean(audiobook.Authors?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
            var authorLastName = GetLastName(author);
            var primaryMembership = audiobook.SeriesMemberships?.FirstOrDefault(membership => membership.IsPrimary)
                ?? audiobook.SeriesMemberships?.FirstOrDefault();
            var series = Clean(audiobook.Series ?? primaryMembership?.SeriesName);
            var seriesNumber = Clean(audiobook.SeriesNumber ?? primaryMembership?.SeriesNumber);
            var isbn = audiobook.Isbn?
                .Select(value => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()))
                .Where(value => value.Length is 10 or 13)
                .OrderByDescending(value => value.Length)
                .FirstOrDefault();

            var queries = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Add(queries, seen, title, author);
            Add(queries, seen, title);
            Add(queries, seen, coreTitle, author);
            Add(queries, seen, coreTitle);
            Add(queries, seen, audiobook.Asin);
            Add(queries, seen, isbn);
            Add(queries, seen, bareTitle, author);
            Add(queries, seen, bareTitle);
            Add(queries, seen, series, seriesNumber, author);
            Add(queries, seen, series, seriesNumber);
            Add(queries, seen, NormalizePunctuation(coreTitle));
            Add(queries, seen, coreTitle, authorLastName);
            Add(queries, seen, subtitle, author);
            Add(queries, seen, series, coreTitle);
            Add(queries, seen, author);

            return queries.Take(Math.Max(1, maxQueries)).ToArray();
        }

        public static string? GetCoreTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var core = TitleTailRegex().Replace(title, string.Empty).Trim();
            return core.Length > 0 ? core : title.Trim();
        }

        public static string? StripLeadingArticle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var stripped = LeadingArticleRegex().Replace(title.Trim(), string.Empty).Trim();
            return stripped.Length > 0 ? stripped : title.Trim();
        }

        private static string? NormalizePunctuation(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : Regex.Replace(value, @"[^\p{L}\p{N}]+", " ").Trim();

        private static string? GetLastName(string? author)
        {
            if (string.IsNullOrWhiteSpace(author))
            {
                return null;
            }

            var tokens = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 1 ? tokens[^1] : null;
        }

        private static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static void Add(List<string> queries, HashSet<string> seen, params string?[] parts)
        {
            var query = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            var key = Regex.Replace(query, @"[^\p{L}\p{N}]+", " ").Trim();
            if (key.Length > 0 && seen.Add(key))
            {
                queries.Add(query);
            }
        }

        // Drops "Title: Subtitle", "Title - Subtitle" and "Title (Series, Book 2)" tails.
        [GeneratedRegex(@"\s*(?::|\s[-–—]\s|\(|\[).*$", RegexOptions.CultureInvariant)]
        private static partial Regex TitleTailRegex();

        [GeneratedRegex(@"^(?:the|a|an)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex LeadingArticleRegex();
    }
}
