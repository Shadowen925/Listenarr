using Listenarr.Application.Audiobookshelf.Models;

namespace Listenarr.Application.Audiobookshelf.Contracts;

public interface IAudiobookshelfIntegrationService
{
    Task<AudiobookshelfImportConnectionSettings> GetConnectionSettingsAsync();

    Task<AudiobookshelfImportConnectionSettings> SaveConnectionSettingsAsync(
        AudiobookshelfImportConnectionSettings settings);

    Task TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookshelfLibrary>> GetLibrariesAsync(
        CancellationToken cancellationToken = default);

    Task<AudiobookshelfImportPreview> PreviewImportAsync(
        string libraryId,
        CancellationToken cancellationToken = default);

    Task<AudiobookshelfImportResult> ImportAsync(
        AudiobookshelfImportRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> TriggerLibraryScanAsync(
        string libraryId,
        CancellationToken cancellationToken = default);

    Task<bool> TriggerScanForAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);

    Task<AudiobookshelfImage> GetItemCoverAsync(
        string itemId,
        CancellationToken cancellationToken = default);
}
