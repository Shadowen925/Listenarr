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
using Listenarr.Application.Search.Matching;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Metadata.Goodreads
{
    /// <summary>
    /// Matches a library entry to Goodreads and fills (never overwrites) missing metadata.
    /// Audible-specific data such as <see cref="Audiobook.Asin"/> is intentionally left untouched.
    /// </summary>
    public sealed class GoodreadsEnrichmentService(
        IAudiobookRepository audiobookRepository,
        IGoodreadsClient goodreadsClient,
        ILogger<GoodreadsEnrichmentService> logger) : IGoodreadsEnrichmentService
    {
        private const int MaxQueries = 4;

        public async Task<GoodreadsEnrichmentResult> EnrichAsync(int audiobookId, CancellationToken ct = default)
        {
            var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null || audiobook.AudiobookshelfMetadataAuthoritative
                || string.IsNullOrWhiteSpace(audiobook.Title))
            {
                return new GoodreadsEnrichmentResult(GoodreadsEnrichmentStatus.Skipped);
            }

            var existingId = audiobook.ExternalIdentifiers?
                .FirstOrDefault(identifier => identifier.Type == AudiobookExternalIdentifierType.GoodreadsId)?.ValueNormalized;
            if (!string.IsNullOrEmpty(existingId))
            {
                return new GoodreadsEnrichmentResult(GoodreadsEnrichmentStatus.Skipped, existingId);
            }

            var candidate = await FindCandidateAsync(audiobook, ct);
            if (candidate == null)
            {
                logger.LogDebug("No Goodreads match for audiobook {Id} '{Title}'", audiobook.Id, audiobook.Title);
                return new GoodreadsEnrichmentResult(GoodreadsEnrichmentStatus.NoMatch);
            }

            var details = await goodreadsClient.GetBookAsync(candidate.BookId, ct);
            Merge(audiobook, details);

            var identifiers = (audiobook.ExternalIdentifiers ?? [])
                .Select(identifier => new AudiobookExternalIdentifier
                {
                    AudiobookId = audiobook.Id,
                    Type = identifier.Type,
                    ValueRaw = identifier.ValueRaw,
                    ValueNormalized = identifier.ValueNormalized,
                    Region = identifier.Region,
                    IsPrimary = identifier.IsPrimary,
                    Source = identifier.Source,
                    CreatedAt = identifier.CreatedAt
                })
                .ToList();
            identifiers.Add(new AudiobookExternalIdentifier
            {
                AudiobookId = audiobook.Id,
                Type = AudiobookExternalIdentifierType.GoodreadsId,
                ValueRaw = candidate.BookId,
                ValueNormalized = candidate.BookId,
                IsPrimary = true,
                Source = AudiobookExternalIdentifierSource.Provider
            });

            await audiobookRepository.UpdateWithIdentifierReplaceAsync(audiobook, identifiers, ct);
            logger.LogInformation("Enriched audiobook {Id} '{Title}' from Goodreads book {BookId}",
                audiobook.Id, audiobook.Title, candidate.BookId);
            return new GoodreadsEnrichmentResult(GoodreadsEnrichmentStatus.Matched, candidate.BookId, true);
        }

        private async Task<GoodreadsSearchCandidate?> FindCandidateAsync(Audiobook audiobook, CancellationToken ct)
        {
            var asin = audiobook.Asin;
            var queries = AudiobookSearchQueryPlanner.Build(audiobook)
                .Where(query => !string.Equals(query, asin, StringComparison.OrdinalIgnoreCase))
                .Where(query => !string.Equals(query, audiobook.Authors?.FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
                .Take(MaxQueries);

            foreach (var query in queries)
            {
                ct.ThrowIfCancellationRequested();
                var results = await goodreadsClient.SearchAsync(query, ct);
                var best = GoodreadsCandidateMatcher.SelectBest(audiobook, results);
                if (best != null)
                {
                    return best;
                }
            }

            return null;
        }

        internal static void Merge(Audiobook audiobook, GoodreadsBookDetails? details)
        {
            if (details == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(audiobook.Description) && !string.IsNullOrWhiteSpace(details.Description))
            {
                audiobook.Description = details.Description;
            }

            if (details.Genres.Count > 0)
            {
                var genres = audiobook.Genres ?? [];
                foreach (var genre in details.Genres)
                {
                    if (!genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    {
                        genres.Add(genre);
                    }
                }

                audiobook.Genres = genres;
            }

            var isbns = audiobook.Isbn ?? [];
            foreach (var isbn in new[] { details.Isbn13, details.Isbn })
            {
                var digits = new string((isbn ?? string.Empty).Where(c => char.IsAsciiDigit(c) || c is 'X' or 'x').ToArray());
                if (digits.Length is 10 or 13 && !isbns.Contains(digits, StringComparer.OrdinalIgnoreCase))
                {
                    isbns.Add(digits);
                }
            }

            audiobook.Isbn = isbns.Count > 0 ? isbns : audiobook.Isbn;

            if (string.IsNullOrWhiteSpace(audiobook.Publisher) && !string.IsNullOrWhiteSpace(details.Publisher))
            {
                audiobook.Publisher = details.Publisher;
            }

            if (string.IsNullOrWhiteSpace(audiobook.Language) && !string.IsNullOrWhiteSpace(details.Language))
            {
                audiobook.Language = details.Language;
            }

            if (details.PublishedUtc is { } published)
            {
                if (string.IsNullOrWhiteSpace(audiobook.PublishedDate))
                {
                    audiobook.PublishedDate = published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                if (string.IsNullOrWhiteSpace(audiobook.PublishYear))
                {
                    audiobook.PublishYear = published.Year.ToString(CultureInfo.InvariantCulture);
                }
            }

            if ((audiobook.Authors == null || audiobook.Authors.Count == 0) && !string.IsNullOrWhiteSpace(details.Author))
            {
                audiobook.Authors = [details.Author];
            }

            var hasSeries = !string.IsNullOrWhiteSpace(audiobook.Series)
                || audiobook.SeriesMemberships?.Count > 0;
            if (!hasSeries && details.Series.Count > 0)
            {
                var series = details.Series[0];
                audiobook.Series = series.Name;
                audiobook.SeriesNumber = series.Position;
            }
        }
    }
}
