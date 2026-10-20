using System.Net;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Search.Providers.Torznab;

[Trait("Name", "TorznabNewznabSearchProviderTests")]
[Trait("Category", "Search")]
public sealed class TorznabNewznabSearchProviderTests : BaseTests
{
    [Fact]
    public async Task SearchAsync_HttpFailure_ThrowsWithoutExposingApiKey()
    {
        const string apiKey = "secret-api-key";
        var handler = new DelegatingHandlerMock((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var provider = new Listenarr.Infrastructure.Search.Providers.Torznab.TorznabNewznabSearchProvider(
            new HttpClient(handler),
            NullLogger<Listenarr.Infrastructure.Search.Providers.Torznab.TorznabNewznabSearchProvider>.Instance);
        var indexer = new Indexer
        {
            Name = "Private indexer",
            Type = "Torznab",
            Implementation = "Torznab",
            Url = "https://indexer.example/api",
            ApiKey = apiKey
        };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SearchAsync(indexer, "Book"));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
    }
}
