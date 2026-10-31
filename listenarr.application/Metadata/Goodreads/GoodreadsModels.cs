/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Metadata.Goodreads
{
    public sealed record GoodreadsSearchCandidate(
        string BookId,
        string Title,
        string? BareTitle,
        string? Author,
        int? RatingsCount);

    public sealed record GoodreadsSeriesInfo(string Name, string? Position);

    public sealed record GoodreadsBookDetails
    {
        public string BookId { get; init; } = string.Empty;
        public string? Title { get; init; }
        public string? Author { get; init; }
        public string? Description { get; init; }
        public string? ImageUrl { get; init; }
        public IReadOnlyList<GoodreadsSeriesInfo> Series { get; init; } = [];
        public IReadOnlyList<string> Genres { get; init; } = [];
        public string? Isbn { get; init; }
        public string? Isbn13 { get; init; }
        public string? Publisher { get; init; }
        public string? Language { get; init; }
        public int? NumPages { get; init; }
        public DateTime? PublishedUtc { get; init; }
        public double? AverageRating { get; init; }
        public int? RatingsCount { get; init; }
    }

    /// <summary>Port for the Goodreads public endpoints (no API key).</summary>
    public interface IGoodreadsClient
    {
        Task<IReadOnlyList<GoodreadsSearchCandidate>> SearchAsync(string query, CancellationToken ct = default);

        Task<GoodreadsBookDetails?> GetBookAsync(string bookId, CancellationToken ct = default);
    }

    public enum GoodreadsEnrichmentStatus
    {
        Skipped,
        NoMatch,
        Matched
    }

    public sealed record GoodreadsEnrichmentResult(GoodreadsEnrichmentStatus Status, string? BookId = null, bool Changed = false);

    public interface IGoodreadsEnrichmentService
    {
        Task<GoodreadsEnrichmentResult> EnrichAsync(int audiobookId, CancellationToken ct = default);
    }
}
