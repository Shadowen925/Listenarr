using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Processing
{
    public partial class DownloadProcessingJobProcessor
    {
        private async Task<(QueueItem Item, List<string> SourceFiles)> ResolveImportItemAsync(
            IServiceProvider serviceProvider,
            Download download,
            bool isDirectDownload,
            CancellationToken cancellationToken)
        {
            var downloadItemService = serviceProvider.GetRequiredService<IDownloadItemService>();
            try
            {
                var queueItem = await downloadItemService.GetImportItemAsync(download, cancellationToken);
                if (queueItem.SourceFiles is not { } sourceFiles)
                {
                    throw new DownloadProcessingException(isDirectDownload
                        ? "Unable to resolve the local direct-download file"
                        : "Unable to fetch the download from the download client");
                }

                return (queueItem, sourceFiles);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadProcessingException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Unable to resolve source files for download {DownloadId}; scheduling an import retry",
                    download.Id);
                throw new DownloadProcessingException(
                    $"Unable to resolve download source files: {exception.Message}",
                    exception);
            }
        }
    }
}
