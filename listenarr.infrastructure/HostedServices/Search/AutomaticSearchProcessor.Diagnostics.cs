using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    public partial class AutomaticSearchProcessor
    {
        private async Task PublishDiagnosticAsync(
            Audiobook audiobook,
            string query,
            string outcome,
            int resultCount,
            int eligibleCount,
            int rejectedCount,
            AutomaticSearchTrigger searchTrigger = AutomaticSearchTrigger.Periodic,
            IReadOnlyList<string>? indexersQueried = null,
            string? selectedRelease = null,
            string? message = null,
            IReadOnlyList<string>? rejectionReasons = null,
            IReadOnlyList<AutomaticSearchSubmissionAttempt>? submissionAttempts = null,
            string? downloadClient = null,
            DateTime? nextRetryAt = null)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            try
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAutomaticSearchDiagnosticRepository>();
                await repository.AddAsync(new AutomaticSearchDiagnostic
                {
                    AudiobookId = audiobook.Id,
                    SearchTimestamp = DateTime.UtcNow,
                    SearchTrigger = searchTrigger.ToString(),
                    IndexersQueried = (indexersQueried ?? Array.Empty<string>())
                        .Select(SanitizeDiagnosticText)
                        .Where(value => value.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(100)
                        .ToList(),
                    ResultsReturned = Math.Max(0, resultCount),
                    RejectionReasons = (rejectionReasons ?? Array.Empty<string>())
                        .Select(SanitizeDiagnosticText)
                        .Where(value => value.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .Take(100)
                        .ToList(),
                    SubmissionAttempts = (submissionAttempts ?? Array.Empty<AutomaticSearchSubmissionAttempt>())
                        .Take(100)
                        .Select(attempt => attempt with
                        {
                            ReleaseTitle = SanitizeDiagnosticText(attempt.ReleaseTitle),
                            ClientType = SanitizeNullableDiagnosticText(attempt.ClientType),
                            FailureReason = SanitizeNullableDiagnosticText(attempt.FailureReason)
                        })
                        .ToList(),
                    SelectedRelease = SanitizeNullableDiagnosticText(selectedRelease),
                    DownloadClient = SanitizeNullableDiagnosticText(downloadClient),
                    FinalOutcome = SanitizeDiagnosticText(outcome),
                    NextRetryAt = nextRetryAt
                });
            }
            catch (Exception ex) when (Listenarr.Application.Common.WorkerExceptionClassifier.IsNonFatal(ex))
            {
                _logger.LogError(
                    ex,
                    "Failed to persist automatic search diagnostics for audiobook {AudiobookId}",
                    audiobook.Id);
            }

            try
            {
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                await hub.Clients.All.SendCoreAsync(
                    "SearchProgress",
                    [
                        new
                        {
                            message = message ?? $"Automatic search for '{audiobook.Title}': {outcome}",
                            type = "automatic",
                            audiobookId = audiobook.Id,
                            details = new
                            {
                                query = SanitizeDiagnosticText(query),
                                outcome,
                                resultCount,
                                eligibleCount,
                                rejectedCount,
                                selectedRelease = SanitizeNullableDiagnosticText(selectedRelease),
                                searchTrigger = searchTrigger.ToString(),
                                indexersQueried = indexersQueried?.Select(SanitizeDiagnosticText).ToArray(),
                                rejectionReasons = rejectionReasons?.Select(SanitizeDiagnosticText).ToArray(),
                                submissionAttempts = submissionAttempts?.Select(attempt => attempt with
                                {
                                    ReleaseTitle = SanitizeDiagnosticText(attempt.ReleaseTitle),
                                    ClientType = SanitizeNullableDiagnosticText(attempt.ClientType),
                                    FailureReason = SanitizeNullableDiagnosticText(attempt.FailureReason)
                                }).ToArray(),
                                downloadClient = SanitizeNullableDiagnosticText(downloadClient),
                                nextRetryAt
                            }
                        }
                    ]);
            }
            catch (Exception ex) when (Listenarr.Application.Common.WorkerExceptionClassifier.IsNonFatal(ex))
            {
                _logger.LogDebug(
                    ex,
                    "Failed to publish automatic search diagnostic for audiobook {AudiobookId}",
                    audiobook.Id);
            }
        }

        private static string SanitizeDiagnosticText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var sanitized = Regex.Replace(
                value,
                @"(?i)\b(?:https?|ftp|magnet)://\S+|\b(?:api[_-]?key|token|password)\s*[:=]\s*\S+",
                "[redacted]");
            return sanitized.Length > 500 ? sanitized[..500] : sanitized;
        }

        private static string? SanitizeNullableDiagnosticText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : SanitizeDiagnosticText(value);
    }
}
