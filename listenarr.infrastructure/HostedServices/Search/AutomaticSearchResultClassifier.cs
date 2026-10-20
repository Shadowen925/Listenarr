/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    internal sealed class AutomaticSearchResultClassifier
    {
        private readonly ILogger _logger;

        public AutomaticSearchResultClassifier(ILogger logger)
        {
            _logger = logger;
        }

        public string BuildSearchQuery(Audiobook audiobook)
        {
            return BuildSearchQueries(audiobook).FirstOrDefault() ?? string.Empty;
        }

        public IReadOnlyList<string> BuildSearchQueries(Audiobook audiobook)
        {
            var title = audiobook.Title?.Trim();
            var author = audiobook.Authors?.FirstOrDefault()?.Trim();
            var primaryMembership = audiobook.SeriesMemberships?.FirstOrDefault(membership => membership.IsPrimary)
                ?? audiobook.SeriesMemberships?.FirstOrDefault();
            var series = (audiobook.Series ?? primaryMembership?.SeriesName)?.Trim();
            var seriesNumber = (audiobook.SeriesNumber ?? primaryMembership?.SeriesNumber)?.Trim();
            var queries = new List<string>();

            AddQuery(queries, title, author, series);
            AddQuery(queries, title, author);
            AddQuery(queries, series, seriesNumber, author);
            AddQuery(queries, title, series);
            AddQuery(queries, audiobook.Asin);

            return queries;
        }

        private static void AddQuery(List<string> queries, params string?[] parts)
        {
            var query = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (!string.IsNullOrWhiteSpace(query)
                && !queries.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(query);
            }
        }

        public bool IsTorrentResult(SearchResult result)
        {
            if (!string.IsNullOrEmpty(result.DownloadType))
            {
                if (string.Equals(result.DownloadType, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                else if (result.DownloadType == "Torrent")
                {
                    return true;
                }
                else if (result.DownloadType == "Usenet")
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
            {
                return true;
            }

            _logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
                result.Title, result.Source);
            return false;
        }
    }
}
