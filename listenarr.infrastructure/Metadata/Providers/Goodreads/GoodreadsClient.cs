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
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Listenarr.Application.Metadata.Goodreads;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Providers.Goodreads
{
    /// <summary>
    /// Reads Goodreads' public auto-complete endpoint and the Next.js state embedded in book pages.
    /// Both are unofficial, so parsing is tolerant and every failure degrades to "no data".
    /// </summary>
    public sealed partial class GoodreadsClient : IGoodreadsClient
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

        private readonly HttpClient _http;
        private readonly ILogger<GoodreadsClient> _logger;

        public GoodreadsClient(HttpClient http, ILogger<GoodreadsClient> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<IReadOnlyList<GoodreadsSearchCandidate>> SearchAsync(string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return [];
            }

            var url = $"https://www.goodreads.com/book/auto_complete?format=json&q={Uri.EscapeDataString(query)}";
            var json = await GetStringAsync(url, ct);
            return json == null ? [] : ParseSearch(json);
        }

        public async Task<GoodreadsBookDetails?> GetBookAsync(string bookId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bookId) || !bookId.All(char.IsAsciiDigit))
            {
                return null;
            }

            var html = await GetStringAsync($"https://www.goodreads.com/book/show/{bookId}", ct);
            return html == null ? null : ParseBookPage(html, bookId);
        }

        private async Task<string?> GetStringAsync(string url, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Goodreads request {Url} returned {Status}", url, (int)response.StatusCode);
                    return null;
                }

                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Goodreads request {Url} failed", url);
                return null;
            }
        }

        public static IReadOnlyList<GoodreadsSearchCandidate> ParseSearch(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var results = new List<GoodreadsSearchCandidate>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = ReadString(item, "bookId");
                    var title = ReadString(item, "title");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    string? author = null;
                    if (item.TryGetProperty("author", out var authorElement) && authorElement.ValueKind == JsonValueKind.Object)
                    {
                        author = ReadString(authorElement, "name");
                    }

                    results.Add(new GoodreadsSearchCandidate(
                        id,
                        title,
                        ReadString(item, "bookTitleBare"),
                        author,
                        ReadInt(item, "ratingsCount")));
                }

                return results;
            }
            catch (JsonException)
            {
                return [];
            }
        }

        public static GoodreadsBookDetails? ParseBookPage(string html, string bookId)
        {
            var match = NextDataRegex().Match(html);
            if (!match.Success)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(match.Groups[1].Value);
                if (!doc.RootElement.TryGetProperty("props", out var props)
                    || !props.TryGetProperty("pageProps", out var pageProps)
                    || !pageProps.TryGetProperty("apolloState", out var state)
                    || state.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                JsonElement? book = null;
                foreach (var entry in state.EnumerateObject())
                {
                    if (entry.Name.StartsWith("Book:", StringComparison.Ordinal)
                        && ReadString(entry.Value, "legacyId") == bookId)
                    {
                        book = entry.Value;
                        break;
                    }
                }

                if (book is not { } node)
                {
                    return null;
                }

                var details = node.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                var hasDetails = details.ValueKind == JsonValueKind.Object;

                string? author = null;
                if (node.TryGetProperty("primaryContributorEdge", out var edge) && edge.ValueKind == JsonValueKind.Object
                    && edge.TryGetProperty("node", out var contributorRef))
                {
                    author = ReadString(Resolve(state, contributorRef), "name");
                }

                var series = new List<GoodreadsSeriesInfo>();
                if (node.TryGetProperty("bookSeries", out var seriesArray) && seriesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in seriesArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("series", out var seriesRef))
                        {
                            var name = ReadString(Resolve(state, seriesRef), "title");
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                series.Add(new GoodreadsSeriesInfo(name.Trim(), ReadString(item, "userPosition")));
                            }
                        }
                    }
                }

                var genres = new List<string>();
                if (node.TryGetProperty("bookGenres", out var genreArray) && genreArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in genreArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("genre", out var genre))
                        {
                            var name = ReadString(genre, "name");
                            if (!string.IsNullOrWhiteSpace(name) && !genres.Contains(name, StringComparer.OrdinalIgnoreCase))
                            {
                                genres.Add(name);
                            }
                        }
                    }
                }

                double? average = null;
                int? ratings = null;
                if (node.TryGetProperty("work", out var workRef))
                {
                    var work = Resolve(state, workRef);
                    if (work.ValueKind == JsonValueKind.Object && work.TryGetProperty("stats", out var stats))
                    {
                        average = ReadDouble(stats, "averageRating");
                        ratings = ReadInt(stats, "ratingsCount");
                    }
                }

                DateTime? published = null;
                if (hasDetails && ReadLong(details, "publicationTime") is { } millis)
                {
                    published = DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
                }

                string? language = null;
                if (hasDetails && details.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.Object)
                {
                    language = ReadString(lang, "name");
                }

                var description = ReadString(node, "description({\"stripped\":true})") ?? StripHtml(ReadString(node, "description"));

                return new GoodreadsBookDetails
                {
                    BookId = bookId,
                    Title = ReadString(node, "titleComplete") ?? ReadString(node, "title"),
                    Author = author,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    ImageUrl = ReadString(node, "imageUrl"),
                    Series = series,
                    Genres = genres,
                    Isbn = hasDetails ? ReadString(details, "isbn") : null,
                    Isbn13 = hasDetails ? ReadString(details, "isbn13") : null,
                    Publisher = hasDetails ? ReadString(details, "publisher") : null,
                    Language = language,
                    NumPages = hasDetails ? ReadInt(details, "numPages") : null,
                    PublishedUtc = published,
                    AverageRating = average,
                    RatingsCount = ratings
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonElement Resolve(JsonElement state, JsonElement reference)
        {
            if (reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("__ref", out var key)
                && key.ValueKind == JsonValueKind.String
                && state.TryGetProperty(key.GetString()!, out var resolved))
            {
                return resolved;
            }

            return reference;
        }

        private static string? StripHtml(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var withBreaks = BreakRegex().Replace(html, "\n");
            return WebUtility.HtmlDecode(TagRegex().Replace(withBreaks, string.Empty));
        }

        private static string? ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }

        private static int? ReadInt(JsonElement element, string name) =>
            ReadLong(element, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

        private static long? ReadLong(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            return value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static double? ReadDouble(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }

            return value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        [GeneratedRegex("<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex NextDataRegex();

        [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex BreakRegex();

        [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
        private static partial Regex TagRegex();
    }
}
