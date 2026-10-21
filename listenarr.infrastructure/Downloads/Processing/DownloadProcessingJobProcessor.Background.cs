using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Processing;

public partial class DownloadProcessingJobProcessor
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Download Processing Background Service waiting for library filesystem initialization");
        await filesystemReadiness.WaitUntilReadyAsync(stoppingToken);
        logger.LogInformation("Download Processing Background Service started");

        try
        {
            using var scope = scopeFactory.CreateScope();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
            await downloadProcessingJobService.ResetStuckJobsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Download processing startup reset canceled during shutdown");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Download processing startup reset canceled/timed out; continuing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to reset stuck jobs on startup");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "Download processing cycle canceled/timed out; continuing");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error processing download queue");
            }

            try
            {
                await Task.Delay(_processingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Download Processing Background Service stopped");
    }
}
