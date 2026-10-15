using Listenarr.Application.Audiobookshelf.Models;

namespace Listenarr.Application.Audiobookshelf.Contracts;

public interface IAudiobookshelfClient
{
    Task TestConnectionAsync(string url, string apiKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookshelfLibrary>> GetLibrariesAsync(
        string url,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookshelfLibraryItem>> GetLibraryItemsAsync(
        string url,
        string apiKey,
        string libraryId,
        CancellationToken cancellationToken = default);

    Task TriggerLibraryScanAsync(
        string url,
        string apiKey,
        string libraryId,
        CancellationToken cancellationToken = default);

    Task<AudiobookshelfImage> GetItemCoverAsync(
        string url,
        string apiKey,
        string itemId,
        CancellationToken cancellationToken = default);
}
