using System.Net;
using System.Text;
using Listenarr.Infrastructure.Audiobookshelf;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Audiobookshelf;

[Trait("Name", nameof(AudiobookshelfApiClientTests))]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookshelfApiClientTests : BaseTests
{
    [Fact]
    public async Task GetLibrariesAsync_UsesBearerAuthenticationAndParsesLibraryFolders()
    {
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        string? requestPath = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            authorizationScheme = request.Headers.Authorization?.Scheme;
            authorizationParameter = request.Headers.Authorization?.Parameter;
            requestPath = request.RequestUri?.AbsolutePath;
            return JsonResponse(
                """
                {
                  "libraries": [
                    {
                      "id": "lib_a",
                      "name": "Audiobooks",
                      "mediaType": " book ",
                      "folders": [{ "fullPath": "/audiobooks" }]
                    }
                  ]
                }
                """);
        }));
        var client = new AudiobookshelfApiClient(httpClient);

        var libraries = await client.GetLibrariesAsync(
            "http://audiobookshelf:13378",
            "private-token");

        var library = Assert.Single(libraries);
        Assert.Equal("lib_a", library.Id);
        Assert.Equal("Audiobooks", library.Name);
        Assert.Equal(["/audiobooks"], library.FolderPaths);
        Assert.Equal("Bearer", authorizationScheme);
        Assert.Equal("private-token", authorizationParameter);
        Assert.Equal("/api/libraries", requestPath);
    }

    [Fact]
    public async Task GetLibraryItemsAsync_ParsesMetadataAndAudioPaths()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "results": [
                {
                  "id": "li_book",
                  "path": "/audiobooks/Author/Book",
                  "media": {
                    "metadata": {
                      "title": "Corrected title",
                      "authors": [{ "name": "Corrected author" }],
                      "series": [{ "name": "Series", "sequence": "2" }],
                      "description": "ABS description",
                      "asin": "B012345678",
                      "isbn": ["9780306406157"],
                      "openlibraryId": "OL123W"
                    },
                    "audioFiles": [
                      { "metadata": { "path": "/audiobooks/Author/Book/part01.m4b" } }
                    ]
                  }
                }
              ]
            }
            """)));
        var client = new AudiobookshelfApiClient(httpClient);

        var items = await client.GetLibraryItemsAsync(
            "http://audiobookshelf:13378",
            "private-token",
            "lib_a");

        var item = Assert.Single(items);
        Assert.Equal("li_book", item.Id);
        Assert.Equal("Corrected title", item.Metadata.Title);
        Assert.Equal(["Corrected author"], item.Metadata.Authors);
        Assert.Equal("Series", item.Metadata.Series);
        Assert.Equal("2", item.Metadata.SeriesNumber);
        Assert.Equal("ABS description", item.Metadata.Description);
        Assert.Equal("OL123W", item.Metadata.OpenLibraryId);
        Assert.Equal(["/audiobooks/Author/Book/part01.m4b"], item.AudioFilePaths);
    }

    [Fact]
    public async Task TestConnectionAsync_RejectsUnsuccessfulResponses()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = new AudiobookshelfApiClient(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TestConnectionAsync("http://audiobookshelf:13378", "bad-token"));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
