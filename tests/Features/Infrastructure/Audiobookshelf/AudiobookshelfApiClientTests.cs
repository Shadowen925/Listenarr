using System.Net;
using System.Text;
using Listenarr.Application.Audiobookshelf.Models;
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
                      "series": [
                        { "name": "Series", "sequence": "2" },
                        { "title": "Shared Universe", "position": 4 }
                      ],
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
        Assert.Equal(
            [
                new AudiobookshelfSeriesMembership("Series", "2"),
                new AudiobookshelfSeriesMembership("Shared Universe", "4")
            ],
            item.Metadata.SeriesMemberships);
        Assert.Equal("ABS description", item.Metadata.Description);
        Assert.Equal("OL123W", item.Metadata.OpenLibraryId);
        Assert.Equal(["/audiobooks/Author/Book/part01.m4b"], item.AudioFilePaths);
    }

    [Fact]
    public async Task GetLibraryItemsAsync_MinifiedListResponse_LoadsAudioFilesFromItemDetails()
    {
        var requested = new List<string>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/api/items/li_book"
                ? JsonResponse(
                    """
                    {
                      "id": "li_book",
                      "path": "/audiobooks/Author/Book",
                      "media": {
                        "metadata": { "title": "T" },
                        "audioFiles": [
                          { "metadata": { "path": "/audiobooks/Author/Book/a.m4b" } }
                        ]
                      }
                    }
                    """)
                : JsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": "li_book",
                          "path": "/audiobooks/Author/Book",
                          "media": { "metadata": { "title": "T" }, "numAudioFiles": 1 }
                        }
                      ]
                    }
                    """);
        }));
        var client = new AudiobookshelfApiClient(httpClient);

        var items = await client.GetLibraryItemsAsync("http://audiobookshelf:13378", "private-token", "lib_a");

        Assert.Equal(["/audiobooks/Author/Book/a.m4b"], Assert.Single(items).AudioFilePaths);
        Assert.Equal(["/api/libraries/lib_a/items", "/api/items/li_book"], requested);
    }

    [Fact]
    public async Task GetLibraryItemsAsync_MergesSeriesFromItemDetailsForMinifiedRecords()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/items/li_book"
                ? JsonResponse(
                    """
                    {
                      "id": "li_book",
                      "path": "/audiobooks/Author/Book",
                      "media": {
                        "metadata": {
                          "title": "Harry Potter and the Philosopher's Stone",
                          "series": ["Wizarding World #1", "Harry Potter #1"]
                        },
                        "audioFiles": [
                          { "metadata": { "path": "/audiobooks/Author/Book/book.m4b" } }
                        ]
                      }
                    }
                    """)
                : JsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": "li_book",
                          "path": "/audiobooks/Author/Book",
                          "media": {
                            "metadata": { "title": "Harry Potter and the Philosopher's Stone" },
                            "numAudioFiles": 1
                          }
                        }
                      ]
                    }
                    """)));
        var client = new AudiobookshelfApiClient(httpClient);

        var item = Assert.Single(await client.GetLibraryItemsAsync(
            "http://audiobookshelf:13378",
            "private-token",
            "lib_a"));

        Assert.Equal(AudiobookshelfSeriesMetadataState.Parsed, item.Metadata.SeriesMetadataState);
        Assert.Equal(
            [
                new AudiobookshelfSeriesMembership("Wizarding World", "1"),
                new AudiobookshelfSeriesMembership("Harry Potter", "1")
            ],
            item.Metadata.SeriesMemberships);
        Assert.Equal("book.m4b", Path.GetFileName(Assert.Single(item.AudioFilePaths)));
    }

    [Fact]
    public async Task GetLibraryItemsAsync_ParsesAudiobookshelfSeriesRepresentationsAndPositions()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "results": [
                {
                  "id": "strings",
                  "media": {
                    "metadata": {
                      "series": ["Wizarding World #1", "Harry Potter #1.5"]
                    },
                    "audioFiles": [{ "path": "/a.m4b" }]
                  }
                },
                {
                  "id": "objects",
                  "media": {
                    "metadata": {
                      "series": [
                        { "id": "abs-series-1", "name": "The First Series", "sequence": 2.5 },
                        { "name": "The Other Series", "position": "3.25" }
                      ]
                    },
                    "audioFiles": [{ "path": "/b.m4b" }]
                  }
                },
                {
                  "id": "single",
                  "media": {
                    "metadata": {
                      "series": { "id": "abs-series-2", "name": "Solo Series", "sequence": "4.5" }
                    },
                    "audioFiles": [{ "path": "/c.m4b" }]
                  }
                },
                {
                  "id": "nested",
                  "media": {
                    "metadata": {
                      "metadata": {
                        "series": [{ "name": "Nested Series", "sequence": "7.5" }]
                      }
                    },
                    "audioFiles": [{ "path": "/d.m4b" }]
                  }
                },
                {
                  "id": "empty",
                  "media": {
                    "metadata": { "series": [] },
                    "audioFiles": [{ "path": "/e.m4b" }]
                  }
                },
                {
                  "id": "malformed",
                  "media": {
                    "metadata": { "series": [true] },
                    "audioFiles": [{ "path": "/f.m4b" }]
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

        var strings = items.Single(item => item.Id == "strings").Metadata;
        Assert.Equal(
            [("Wizarding World", "1"), ("Harry Potter", "1.5")],
            strings.SeriesMemberships!
                .Select(membership => (membership.Name, membership.Position))
                .ToArray());

        var objects = items.Single(item => item.Id == "objects").Metadata;
        Assert.Equal("abs-series-1", objects.SeriesMemberships![0].SourceId);
        Assert.Equal("2.5", objects.SeriesMemberships[0].Position);
        Assert.Equal("3.25", objects.SeriesMemberships[1].Position);

        var single = Assert.Single(items.Single(item => item.Id == "single").Metadata.SeriesMemberships!);
        Assert.Equal("abs-series-2", single.SourceId);
        Assert.Equal("Solo Series", single.Name);
        Assert.Equal("4.5", single.Position);
        Assert.Equal("Nested Series", Assert.Single(
            items.Single(item => item.Id == "nested").Metadata.SeriesMemberships!).Name);
        Assert.Equal(
            AudiobookshelfSeriesMetadataState.NoSeries,
            items.Single(item => item.Id == "empty").Metadata.SeriesMetadataState);
        Assert.Equal(
            AudiobookshelfSeriesMetadataState.ParseError,
            items.Single(item => item.Id == "malformed").Metadata.SeriesMetadataState);
    }

    [Fact]
    public async Task GetLibraryItemsAsync_ReportsDetailRequestFailurePerItem()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/items/li_book"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": "li_book",
                          "media": {
                            "metadata": { "title": "Book" },
                            "numAudioFiles": 1
                          }
                        }
                      ]
                    }
                    """)));
        var client = new AudiobookshelfApiClient(httpClient);

        var item = Assert.Single(await client.GetLibraryItemsAsync(
            "http://audiobookshelf:13378",
            "private-token",
            "lib_a"));

        Assert.NotNull(item.SourceUnavailableReason);
        Assert.Equal(AudiobookshelfSeriesMetadataState.NotProvided, item.Metadata.SeriesMetadataState);
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
