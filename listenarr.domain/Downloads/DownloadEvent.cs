namespace Listenarr.Domain.Downloads
{
    /// <summary>Bounded audit trail of download lifecycle events. Never stores credentials or URLs.</summary>
    public class DownloadEvent
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string EventType { get; set; } = string.Empty;
        public string? DownloadId { get; set; }
        public int? AudiobookId { get; set; }
        public string? ClientId { get; set; }
        public string? ExternalId { get; set; }
        public string? ReleaseId { get; set; }
        public string? Title { get; set; }
        public string Outcome { get; set; } = DownloadEventOutcomes.Success;
        public string? Message { get; set; }
    }

    public static class DownloadEventOutcomes
    {
        public const string Success = "Success";
        public const string Failure = "Failure";
        public const string Info = "Info";
    }

    public static class DownloadEventTypes
    {
        public const string Submitted = "Submitted";
        public const string MetadataRetrievalStarted = "MetadataRetrievalStarted";
        public const string ProgressUpdated = "ProgressUpdated";
        public const string Stalled = "Stalled";
        public const string ReplacementSelected = "ReplacementSelected";
        public const string PreviousRemoved = "PreviousRemoved";
        public const string ReplacementSubmitted = "ReplacementSubmitted";
        public const string ReplacementFailed = "ReplacementFailed";
        public const string Completed = "Completed";
        public const string ImportStarted = "ImportStarted";
        public const string ImportCompleted = "ImportCompleted";
        public const string ImportFailed = "ImportFailed";
        public const string ManualRemoval = "ManualRemoval";
    }
}
