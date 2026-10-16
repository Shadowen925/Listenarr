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
        var libraries = AudiobookshelfPayloadParser.GetArray(document.RootElement, "libraries");
        return libraries.Select(AudiobookshelfPayloadParser.ParseLibrary).ToList();
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
        var items = AudiobookshelfPayloadParser.GetArray(
            document.RootElement,
            "results",
            "libraryItems",
            "items");
        var parsed = items.Select(AudiobookshelfPayloadParser.ParseLibraryItem).ToList();

        // The Audiobookshelf list endpoint returns minified media (counts only, no audioFiles),
        // so file paths must be read from each item's detail record. Bounded and read-only.
        using var gate = new SemaphoreSlim(MaxDetailRequests);
        var enriched = await Task.WhenAll(parsed.Select(async item =>
        {
            if (item.AudioFilePaths.Count > 0
                && item.Metadata.SeriesMetadataState != AudiobookshelfSeriesMetadataState.NotProvided)
            {
                return item;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                return await GetItemWithFilesAsync(url, apiKey, item, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return item with
                {
                    SourceUnavailableReason = "Audiobookshelf could not retrieve this item's detail record."
                };
            }
            catch (JsonException)
            {
                return item with
                {
                    SourceUnavailableReason = "Audiobookshelf returned an unreadable detail record."
                };
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
        var detailed = AudiobookshelfPayloadParser.ParseLibraryItem(document.RootElement);
        return item with
        {
            Path = string.IsNullOrWhiteSpace(item.Path) ? detailed.Path : item.Path,
            AudioFilePaths = detailed.AudioFilePaths.Count > 0
                ? detailed.AudioFilePaths
                : item.AudioFilePaths,
            Metadata = AudiobookshelfMetadataMerger.Merge(item.Metadata, detailed.Metadata),
            SourceUnavailableReason = null
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

}
