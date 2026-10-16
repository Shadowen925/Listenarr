namespace Listenarr.Application.Downloads.Cleanup
{
    public enum DownloadRemovalMode
    {
        /// <summary>Remove only Listenarr's record; the download client is untouched.</summary>
        ListenarrOnly,

        /// <summary>Remove from the client and Listenarr, keeping any downloaded data.</summary>
        ClientKeepFiles,

        /// <summary>Remove from the client and Listenarr, asking the client to delete partial data.</summary>
        ClientDeleteData,
    }

    public static class DownloadRemovalErrorCodes
    {
        public const string NotFound = "not_found";
        public const string ClientMismatch = "client_mismatch";
        public const string ClientUnavailable = "client_unavailable";
        public const string MissingRemote = "missing_remote";
        public const string ClientFailed = "client_failed";
        public const string UnsafeDelete = "unsafe_delete";
        public const string NoClientIdentity = "no_client_identity";
    }

    public sealed record DownloadRemovalResult(
        bool Success,
        bool RemovedFromClient,
        bool RemovedFromListenarr,
        bool? ClientHadItem,
        string Message,
        string? ErrorCode = null)
    {
        public static DownloadRemovalResult Failure(string code, string message, bool? clientHadItem = null) =>
            new(false, false, false, clientHadItem, message, code);
    }
}
