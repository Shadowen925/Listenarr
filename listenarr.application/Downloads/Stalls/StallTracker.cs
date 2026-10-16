using System.Globalization;

namespace Listenarr.Application.Downloads.Stalls
{
    public enum StallVerdict
    {
        None,
        MetadataPending,
        Stalled,
        MissingFromClient,
    }

    public static class StallMetadataKeys
    {
        public const string Observed = "Stall.Observed";
        public const string LastProgressBytes = "Stall.LastProgressBytes";
        public const string LastProgressAt = "Stall.LastProgressAt";
        public const string LastPollAt = "Stall.LastPollAt";
        public const string State = "Stall.State";
        public const string Since = "Stall.Since";
        public const string ReplacementAttempts = "Stall.ReplacementAttempts";
        public const string Reason = "Stall.Reason";
        public const string MissingSince = "Stall.MissingSince";
        public const string Blocklisted = "Stall.Blocklisted";
        public const string ReplacementState = "Replacement.State";
        public const string ReplacementTarget = "Replacement.Target";
        public const string ReplacementError = "Replacement.Error";
        public const string NextReplacementAt = "Stall.NextReplacementAt";
    }

    /// <summary>
    /// Pure stall evaluation over Download.Metadata so progress tracking survives restarts without a
    /// schema change. Call only after a *successful* client poll; a failed poll must never reach here.
    /// </summary>
    public static class StallTracker
    {
        public static bool IsUsenetClient(DownloadClientConfiguration client) =>
            client.Type.Equals("sabnzbd", StringComparison.OrdinalIgnoreCase)
            || client.Type.Equals("nzbget", StringComparison.OrdinalIgnoreCase);

        /// <summary>Evaluates a download that the client returned in this poll.</summary>
        public static StallVerdict EvaluateObserved(Download download, DownloadClientConfiguration client, StalledDownloadPolicy policy, DateTime nowUtc)
        {
            download.Metadata[StallMetadataKeys.LastPollAt] = Format(nowUtc);
            download.Metadata.Remove(StallMetadataKeys.MissingSince);

            if (!policy.Enabled || IsUsenetClient(client) || download.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused))
            {
                ClearState(download);
                return StallVerdict.None;
            }

            var lastBytes = ReadLong(download, StallMetadataKeys.LastProgressBytes);
            var lastProgressAt = ReadDate(download, StallMetadataKeys.LastProgressAt);
            if (lastProgressAt == null
                || lastBytes == null
                || download.DownloadedSize >= lastBytes.Value + policy.MinimumProgressBytes
                || download.DownloadedSize < lastBytes.Value)
            {
                download.Metadata[StallMetadataKeys.LastProgressBytes] = download.DownloadedSize.ToString(CultureInfo.InvariantCulture);
                download.Metadata[StallMetadataKeys.LastProgressAt] = Format(nowUtc);
                ClearState(download);
                return StallVerdict.None;
            }

            if (download.Status == DownloadStatus.Paused && policy.IgnoreManuallyPaused)
            {
                // Paused time does not count toward a stall.
                download.Metadata[StallMetadataKeys.LastProgressAt] = Format(nowUtc);
                ClearState(download);
                return StallVerdict.None;
            }

            var idle = nowUtc - lastProgressAt.Value;
            var metadataPending = download.TotalSize <= 0 && download.DownloadedSize <= 0;
            var verdict = metadataPending
                ? (idle >= TimeSpan.FromMinutes(policy.MetadataTimeoutMinutes) ? StallVerdict.Stalled : StallVerdict.MetadataPending)
                : (idle >= TimeSpan.FromMinutes(policy.StalledTimeoutMinutes) ? StallVerdict.Stalled : StallVerdict.None);

            ApplyState(download, verdict, metadataPending, idle, nowUtc);
            return verdict;
        }

        /// <summary>
        /// Evaluates a tracked download that a successful poll did not return. This is a diagnostic only
        /// (the client queue view may be category filtered) and never triggers replacement.
        /// </summary>
        public static StallVerdict EvaluateMissing(Download download, DownloadClientConfiguration client, StalledDownloadPolicy policy, DateTime nowUtc)
        {
            if (!policy.Enabled || IsUsenetClient(client) || download.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading))
            {
                return StallVerdict.None;
            }

            var since = ReadDate(download, StallMetadataKeys.MissingSince);
            if (since == null)
            {
                download.Metadata[StallMetadataKeys.MissingSince] = Format(nowUtc);
                return StallVerdict.None;
            }

            if (nowUtc - since.Value < TimeSpan.FromMinutes(policy.MetadataTimeoutMinutes))
            {
                return StallVerdict.None;
            }

            download.Metadata[StallMetadataKeys.State] = nameof(StallVerdict.MissingFromClient);
            download.Metadata[StallMetadataKeys.Reason] =
                "The client did not report this download in its queue. It may have been removed, or its label/category no longer matches this client's filter.";
            return StallVerdict.MissingFromClient;
        }

        public static int GetReplacementAttempts(Download download) =>
            (int?)ReadLong(download, StallMetadataKeys.ReplacementAttempts) ?? 0;

        public static string? GetState(Download download) => download.GetMetadataString(StallMetadataKeys.State);

        private static void ApplyState(Download download, StallVerdict verdict, bool metadataPending, TimeSpan idle, DateTime nowUtc)
        {
            if (verdict == StallVerdict.None)
            {
                ClearState(download);
                return;
            }

            var name = verdict.ToString();
            if (GetState(download) != name || ReadDate(download, StallMetadataKeys.Since) == null)
            {
                download.Metadata[StallMetadataKeys.Since] = Format(nowUtc);
            }

            download.Metadata[StallMetadataKeys.State] = name;
            download.Metadata[StallMetadataKeys.Reason] = verdict == StallVerdict.MetadataPending
                ? "Waiting for the torrent metadata."
                : metadataPending
                    ? $"Torrent metadata was not retrieved within {(int)idle.TotalMinutes} minutes."
                    : $"No bytes downloaded for {(int)idle.TotalMinutes} minutes.";
        }

        private static void ClearState(Download download)
        {
            var current = GetState(download);
            if (current is nameof(StallVerdict.Stalled) or nameof(StallVerdict.MetadataPending) or nameof(StallVerdict.MissingFromClient))
            {
                download.Metadata.Remove(StallMetadataKeys.State);
                download.Metadata.Remove(StallMetadataKeys.Since);
                download.Metadata.Remove(StallMetadataKeys.Reason);
            }
        }

        private static string Format(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        public static DateTime? ReadDate(Download download, string key) =>
            DateTime.TryParse(download.GetMetadataString(key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
                ? value
                : null;

        public static long? ReadLong(Download download, string key) =>
            long.TryParse(download.GetMetadataString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
