namespace Listenarr.Application.Configuration.Core
{
    /// <summary>Rejects out-of-range stalled-download settings before they are persisted.</summary>
    public static class StalledSettingsValidator
    {
        public static void Validate(ApplicationSettings settings)
        {
            Range(settings.StalledTimeoutMinutes, 5, 10080, nameof(settings.StalledTimeoutMinutes));
            Range(settings.StalledMetadataTimeoutMinutes, 1, 1440, nameof(settings.StalledMetadataTimeoutMinutes));
            Range(settings.StalledMaxReplacementAttempts, 0, 10, nameof(settings.StalledMaxReplacementAttempts));
            Range(settings.StalledRetryDelayMinutes, 0, 1440, nameof(settings.StalledRetryDelayMinutes));
            Range(settings.StalledMinimumProgressBytes, 1, 1_000_000_000, nameof(settings.StalledMinimumProgressBytes));

            if (!string.IsNullOrWhiteSpace(settings.StalledCleanupBehavior))
            {
                var value = settings.StalledCleanupBehavior.Trim().ToLowerInvariant();
                if (value is not (StalledDownloadPolicy.CleanupNone or StalledDownloadPolicy.CleanupRemove or StalledDownloadPolicy.CleanupRemoveAndDelete))
                {
                    throw new ArgumentException("StalledCleanupBehavior must be one of: none, remove, remove_and_delete.");
                }
            }
        }

        private static void Range(long? value, long min, long max, string name)
        {
            if (value is { } v && (v < min || v > max))
            {
                throw new ArgumentException($"{name} must be between {min} and {max}.");
            }
        }
    }
}
