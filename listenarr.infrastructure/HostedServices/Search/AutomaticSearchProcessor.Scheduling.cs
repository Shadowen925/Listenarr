using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search;

public partial class AutomaticSearchProcessor
{
    public async Task SearchAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken,
        AutomaticSearchTrigger trigger = AutomaticSearchTrigger.Immediate)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
        if (audiobook == null)
        {
            _logger.LogWarning("Cannot immediately search missing audiobook {AudiobookId}", audiobookId);
            return;
        }

        if (!audiobook.Monitored)
        {
            _logger.LogInformation(
                "Skipping immediate search for unmonitored audiobook '{Title}' (ID: {AudiobookId})",
                audiobook.Title,
                audiobook.Id);
            return;
        }

        var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
        var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
        var releaseRejections = scope.ServiceProvider.GetRequiredService<IReleaseRejectionRepository>();
        var quickAddOperations = scope.ServiceProvider.GetRequiredService<IQuickAddOperationRepository>();
        var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        var indexerRepository = scope.ServiceProvider.GetRequiredService<IIndexerRepository>();
        var settings = await configurationService.GetApplicationSettingsAsync();
        ValidateSearchInterval(settings.AutomaticSearchIntervalHours);
        var indexersQueried = (await indexerRepository.GetEnabledAsync(true, cancellationToken))
            .Select(indexer => indexer.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        _logger.LogInformation(
            "Starting immediate automatic search for audiobook '{Title}' (ID: {AudiobookId})",
            audiobook.Title,
            audiobook.Id);

        var queued = await ProcessAudiobookAsync(
            audiobook,
            searchService,
            qualityProfileService,
            downloadService,
            audiobookRepository,
            downloadRepository,
            fileRepository,
            releaseRejections,
            quickAddOperations,
            configurationService,
            indexersQueried,
            trigger,
            settings.AutomaticSearchIntervalHours,
            cancellationToken);

        audiobook.LastSearchTime = DateTime.UtcNow;
        await audiobookRepository.UpdateAsync(audiobook);
        _logger.LogInformation(
            "Immediate automatic search completed for audiobook '{Title}' (ID: {AudiobookId}); queued {QueuedCount} download(s)",
            audiobook.Title,
            audiobook.Id,
            queued);
    }

    private async Task PerformAutomaticSearchesAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting automatic search cycle for monitored audiobooks");

        using var scope = _serviceScopeFactory.CreateScope();
        var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
        var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
        var releaseRejections = scope.ServiceProvider.GetRequiredService<IReleaseRejectionRepository>();
        var quickAddOperations = scope.ServiceProvider.GetRequiredService<IQuickAddOperationRepository>();
        var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        var indexerRepository = scope.ServiceProvider.GetRequiredService<IIndexerRepository>();
        var settings = await configurationService.GetApplicationSettingsAsync();
        ValidateSearchInterval(settings.AutomaticSearchIntervalHours);

        var cutoffTime = DateTime.UtcNow.AddHours(-settings.AutomaticSearchIntervalHours);
        var monitoredAudiobooks = await audiobookRepository.GetMonitoredAudiobooksForSearchAsync(cutoffTime, stoppingToken);
        var indexersQueried = (await indexerRepository.GetEnabledAsync(true, stoppingToken))
            .Select(indexer => indexer.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        _logger.LogInformation("Found {Count} monitored audiobooks eligible for automatic search", monitoredAudiobooks.Count);

        if (!monitoredAudiobooks.Any())
        {
            _logger.LogInformation("No audiobooks require automatic search at this time");
            return;
        }

        var processedCount = 0;
        var downloadsQueued = 0;

        foreach (var audiobook in monitoredAudiobooks)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                var downloadsQueuedForBook = await ProcessAudiobookAsync(
                    audiobook, searchService, qualityProfileService, downloadService, audiobookRepository,
                    downloadRepository, fileRepository, releaseRejections, quickAddOperations, configurationService, indexersQueried,
                    AutomaticSearchTrigger.Periodic, settings.AutomaticSearchIntervalHours, stoppingToken);

                downloadsQueued += downloadsQueuedForBook;
                processedCount++;

                audiobook.LastSearchTime = DateTime.UtcNow;
                await audiobookRepository.UpdateAsync(audiobook);

                _logger.LogInformation("Processed audiobook '{Title}' - queued {QueuedCount} downloads",
                    audiobook.Title, downloadsQueuedForBook);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Automatic search processing canceled/timed out for audiobook '{Title}' (ID: {Id})", audiobook.Title, audiobook.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error processing audiobook '{Title}' (ID: {Id})", audiobook.Title, audiobook.Id);
            }
        }

        _logger.LogInformation("Automatic search cycle completed. Processed {ProcessedCount} audiobooks, queued {DownloadsQueued} total downloads",
            processedCount, downloadsQueued);
    }
}
