using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Domain.ActivityHistory;

public enum QuickAddOperationState
{
    SearchPending,
    Searching,
    EvaluatingReleases,
    DownloadSubmissionPending,
    Queued,
    Downloading,
    DownloadCompleted,
    ImportPending,
    Importing,
    Verifying,
    NoMatchingReleases,
    FailedNeedsAttention,
    Imported
}

public sealed class QuickAddOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RequestKey { get; set; } = string.Empty;
    public int AudiobookId { get; set; }
    public string AudiobookTitle { get; set; } = string.Empty;
    public QuickAddOperationState State { get; set; } = QuickAddOperationState.SearchPending;
    public int Attempt { get; set; }
    public int? DiagnosticId { get; set; }
    public string? SelectedRelease { get; set; }
    public string? DownloadClientId { get; set; }
    public string? DownloadId { get; set; }
    public string? ProcessingJobId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string CreateRequestKey(Audiobook audiobook)
    {
        ArgumentNullException.ThrowIfNull(audiobook);

        var identity = !string.IsNullOrWhiteSpace(audiobook.Asin)
            ? $"asin:{Normalize(audiobook.Asin)}"
            : string.Join(
                '|',
                Normalize(audiobook.Title),
                string.Join(',', (audiobook.Authors ?? [])
                    .Select(Normalize)
                    .Where(value => value.Length > 0)
                    .Order(StringComparer.Ordinal)),
                Normalize(audiobook.Language),
                Normalize(audiobook.Edition),
                Normalize(audiobook.Version),
                audiobook.ProductionType,
                audiobook.Abridged);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static int StateOrder(QuickAddOperationState state) => state switch
    {
        QuickAddOperationState.SearchPending => 0,
        QuickAddOperationState.Searching => 1,
        QuickAddOperationState.EvaluatingReleases => 2,
        QuickAddOperationState.DownloadSubmissionPending => 3,
        QuickAddOperationState.Queued => 4,
        QuickAddOperationState.Downloading => 5,
        QuickAddOperationState.DownloadCompleted => 6,
        QuickAddOperationState.ImportPending => 7,
        QuickAddOperationState.Importing => 8,
        QuickAddOperationState.Verifying => 9,
        QuickAddOperationState.NoMatchingReleases => 10,
        QuickAddOperationState.FailedNeedsAttention => 11,
        QuickAddOperationState.Imported => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
