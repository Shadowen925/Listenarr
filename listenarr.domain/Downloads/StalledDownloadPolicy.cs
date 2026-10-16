using Listenarr.Domain.Configuration;

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// Effective stalled-download handling rules for one download client. Global defaults come
    /// from <see cref="ApplicationSettings"/>; a client can override individual values through
    /// <see cref="DownloadClientConfiguration.Settings"/>.
    /// </summary>
    public sealed record StalledDownloadPolicy
    {
        public const string CleanupNone = "none";
        public const string CleanupRemove = "remove";
        public const string CleanupRemoveAndDelete = "remove_and_delete";

        public bool Enabled { get; init; } = true;
        public int StalledTimeoutMinutes { get; init; } = 60;
        public int MetadataTimeoutMinutes { get; init; } = 15;
        public int MaxReplacementAttempts { get; init; } = 3;
        public bool AutoSearchAlternatives { get; init; }
        public bool IgnoreManuallyPaused { get; init; } = true;
        public long MinimumProgressBytes { get; init; } = 1;
        public string CleanupBehavior { get; init; } = CleanupNone;
        public bool AutoBlocklist { get; init; } = true;
        public int RetryDelayMinutes { get; init; } = 5;

        public static StalledDownloadPolicy Resolve(ApplicationSettings? settings, DownloadClientConfiguration? client)
        {
            var policy = new StalledDownloadPolicy
            {
                Enabled = settings?.StalledHandlingEnabled ?? true,
                StalledTimeoutMinutes = Positive(settings?.StalledTimeoutMinutes, 60),
                MetadataTimeoutMinutes = Positive(settings?.StalledMetadataTimeoutMinutes, 15),
                MaxReplacementAttempts = NonNegative(settings?.StalledMaxReplacementAttempts, 3),
                AutoSearchAlternatives = settings?.StalledAutoSearchAlternatives ?? false,
                IgnoreManuallyPaused = settings?.StalledIgnoreManuallyPaused ?? true,
                MinimumProgressBytes = Math.Max(1, settings?.StalledMinimumProgressBytes ?? 1),
                CleanupBehavior = NormalizeCleanup(settings?.StalledCleanupBehavior),
                AutoBlocklist = settings?.StalledAutoBlocklist ?? true,
                RetryDelayMinutes = NonNegative(settings?.StalledRetryDelayMinutes, 5),
            };

            var overrides = client?.Settings;
            if (overrides == null || overrides.Count == 0)
            {
                return policy;
            }

            return policy with
            {
                Enabled = ReadBool(overrides, "stalledEnabled") ?? policy.Enabled,
                StalledTimeoutMinutes = Positive(ReadInt(overrides, "stalledTimeoutMinutes"), policy.StalledTimeoutMinutes),
                MetadataTimeoutMinutes = Positive(ReadInt(overrides, "stalledMetadataTimeoutMinutes"), policy.MetadataTimeoutMinutes),
                MaxReplacementAttempts = NonNegative(ReadInt(overrides, "stalledMaxReplacementAttempts"), policy.MaxReplacementAttempts),
                AutoSearchAlternatives = ReadBool(overrides, "stalledAutoSearchAlternatives") ?? policy.AutoSearchAlternatives,
                CleanupBehavior = NormalizeCleanup(ReadString(overrides, "stalledCleanupBehavior") ?? policy.CleanupBehavior),
                MinimumProgressBytes = Math.Max(1, ReadLong(overrides, "stalledMinimumProgressBytes") ?? policy.MinimumProgressBytes),
                IgnoreManuallyPaused = ReadBool(overrides, "stalledIgnoreManuallyPaused") ?? policy.IgnoreManuallyPaused,
                AutoBlocklist = ReadBool(overrides, "stalledAutoBlocklist") ?? policy.AutoBlocklist,
                RetryDelayMinutes = NonNegative(ReadInt(overrides, "stalledRetryDelayMinutes"), policy.RetryDelayMinutes),
            };
        }

        public static string NormalizeCleanup(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            CleanupNone => CleanupNone,
            CleanupRemoveAndDelete => CleanupRemoveAndDelete,
            CleanupRemove => CleanupRemove,
            _ => CleanupNone,
        };

        private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;

        private static int NonNegative(int? value, int fallback) => value is >= 0 ? value.Value : fallback;

        private static string? ReadString(Dictionary<string, object> settings, string key) =>
            settings.TryGetValue(key, out var raw) ? raw?.ToString() : null;

        private static int? ReadInt(Dictionary<string, object> settings, string key) =>
            int.TryParse(ReadString(settings, key), out var value) ? value : null;

        private static long? ReadLong(Dictionary<string, object> settings, string key) =>
            long.TryParse(ReadString(settings, key), out var value) ? value : null;

        private static bool? ReadBool(Dictionary<string, object> settings, string key) =>
            bool.TryParse(ReadString(settings, key), out var value) ? value : null;
    }
}
