using System.Net.Http.Headers;
using System.Text.Json;
using Listenarr.Application.Audiobookshelf.Contracts;
using Listenarr.Application.Audiobookshelf.Models;

namespace Listenarr.Infrastructure.Audiobookshelf;

public sealed class AudiobookshelfApiClient(HttpClient httpClient) : IAudiobookshelfClient
{
    private const int MaxDetailRequests = 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task TestConnectionAsync(
        string url,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            url,
            apiKey,
            HttpMethod.Get,
            "api/libraries",
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<AudiobookshelfLibrary>> GetLibrariesAsync(
        string url,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            url,
            apiKey,
            HttpMethod.Get,
            "api/libraries",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var libraries = GetArray(document.RootElement, "libraries");
        return libraries.Select(ParseLibrary).ToList();
    }

    public async Task<IReadOnlyList<AudiobookshelfLibraryItem>> GetLibraryItemsAsync(
        string url,
        string apiKey,
        string libraryId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            url,
            apiKey,
            HttpMethod.Get,
            $"api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit=0",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var items = GetArray(document.RootElement, "results", "libraryItems", "items");
        var parsed = items.Select(ParseLibraryItem).ToList();

        // The Audiobookshelf list endpoint returns minified media (counts only, no audioFiles),
        // so file paths must be read from each item's detail record. Bounded and read-only.
        using var gate = new SemaphoreSlim(MaxDetailRequests);
        var enriched = await Task.WhenAll(parsed.Select(async item =>
        {
            if (item.AudioFilePaths.Count > 0)
            {
                return item;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                return await GetItemWithFilesAsync(url, apiKey, item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
        return enriched;
    }

    private async Task<AudiobookshelfLibraryItem> GetItemWithFilesAsync(
        string url,
        string apiKey,
        AudiobookshelfLibraryItem item,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            url,
            apiKey,
            HttpMethod.Get,
            $"api/items/{Uri.EscapeDataString(item.Id)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var detailed = ParseLibraryItem(document.RootElement);
        return item with
        {
            Path = string.IsNullOrWhiteSpace(item.Path) ? detailed.Path : item.Path,
            AudioFilePaths = detailed.AudioFilePaths
        };
    }

    public async Task TriggerLibraryScanAsync(
        string url,
        string apiKey,
        string libraryId,
        CancellationToken cancellationToken = default)
    {
        using var requestContent = new StringContent("{}");
        using var request = CreateRequest(
            url,
            apiKey,
            HttpMethod.Post,
            $"api/libraries/{Uri.EscapeDataString(libraryId)}/scan",
            requestContent);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<AudiobookshelfImage> GetItemCoverAsync(
        string url,
        string apiKey,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            url,
            apiKey,
            HttpMethod.Get,
            $"api/items/{Uri.EscapeDataString(itemId)}/cover",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType
            ?? "application/octet-stream";
        return new AudiobookshelfImage(
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            contentType);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url,
        string apiKey,
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(url, apiKey, method, relativePath);
        return await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        string url,
        string apiKey,
        HttpMethod method,
        string relativePath,
        HttpContent? content = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("Audiobookshelf URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains('\r') || apiKey.Contains('\n'))
        {
            throw new ArgumentException("A valid Audiobookshelf API key is required.", nameof(apiKey));
        }

        var normalizedBase = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        var request = new HttpRequestMessage(method, new Uri(normalizedBase, relativePath))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static AudiobookshelfLibrary ParseLibrary(JsonElement element)
    {
        var folderPaths = GetArray(element, "folders")
            .Select(folder => GetString(folder, "fullPath") ?? GetString(folder, "path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new AudiobookshelfLibrary(
            GetString(element, "id") ?? throw new JsonException("Audiobookshelf library has no id."),
            GetString(element, "name") ?? "Audiobookshelf Library",
            GetString(element, "mediaType"),
            folderPaths);
    }

    private static AudiobookshelfLibraryItem ParseLibraryItem(JsonElement element)
    {
        var media = GetProperty(element, "media");
        var metadata = GetProperty(media, "metadata");
        var seriesMemberships = ParseSeries(GetProperty(metadata, "series"));
        var primarySeries = seriesMemberships.FirstOrDefault();
        var audioFiles = GetArray(media, "audioFiles")
            .Select(file =>
            {
                var fileMetadata = GetProperty(file, "metadata");
                return GetString(fileMetadata, "path") ?? GetString(file, "path");
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        return new AudiobookshelfLibraryItem(
            GetString(element, "id") ?? throw new JsonException("Audiobookshelf item has no id."),
            GetString(element, "path") ?? string.Empty,
            audioFiles,
            new AudiobookshelfMetadata(
                GetString(metadata, "title"),
                GetString(metadata, "subtitle"),
                GetStringList(metadata, "authors", "authorName"),
                GetStringList(metadata, "narrators", "narratorName"),
                primarySeries?.Name,
                primarySeries?.Position,
                GetString(metadata, "description"),
                GetString(media, "coverPath"),
                GetString(metadata, "asin"),
                GetStringList(metadata, "isbn"),
                GetString(metadata, "openlibraryId") ?? GetString(metadata, "openLibraryId"),
                GetString(metadata, "publisher"),
                GetString(metadata, "language"),
                GetString(metadata, "publishedYear"),
                GetStringList(metadata, "genres"),
                seriesMemberships));
    }

    private static IReadOnlyList<AudiobookshelfSeriesMembership> ParseSeries(JsonElement series)
    {
        if (series.ValueKind == JsonValueKind.String)
        {
            var name = series.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(name)
                ? []
                : [new AudiobookshelfSeriesMembership(name, null)];
        }

        IEnumerable<JsonElement> entries = series.ValueKind switch
        {
            JsonValueKind.Array => series.EnumerateArray(),
            JsonValueKind.Object => [series],
            _ => []
        };

        return entries
            .Select(entry =>
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    return new AudiobookshelfSeriesMembership(
                        entry.GetString()?.Trim() ?? string.Empty,
                        null);
                }

                var name = GetString(entry, "name") ?? GetString(entry, "title");
                return new AudiobookshelfSeriesMembership(
                    name?.Trim() ?? string.Empty,
                    GetString(entry, "sequence")
                        ?? GetString(entry, "position")
                        ?? GetString(entry, "bookNumber"));
            })
            .Where(membership => !string.IsNullOrWhiteSpace(membership.Name))
            .DistinctBy(
                membership => $"{membership.Name}\u001f{membership.Position}",
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> GetStringList(
        JsonElement element,
        string propertyName,
        string? fallbackPropertyName = null)
    {
        var value = GetProperty(element, propertyName);
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(entry => entry.ValueKind == JsonValueKind.String
                    ? entry.GetString()
                    : GetString(entry, "name"))
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry!)
                .ToList();
        }

        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return [value.GetString()!];
        }

        var fallback = fallbackPropertyName == null
            ? null
            : GetString(element, fallbackPropertyName);
        return string.IsNullOrWhiteSpace(fallback) ? [] : [fallback];
    }

    private static IEnumerable<JsonElement> GetArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = GetProperty(element, propertyName);
            if (property.ValueKind == JsonValueKind.Array)
            {
                return property.EnumerateArray().ToList();
            }
        }

        return [];
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
            ? property
            : default;

    private static string? GetString(JsonElement element, string propertyName)
    {
        var value = GetProperty(element, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
