namespace Listenarr.Infrastructure.Persistence;

internal static class PhysicalIdentityObservationTime
{
    public static DateTime RequireUtcForStorage(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException(
                "Physical identity observations must be stored as UTC.");
        }

        return value;
    }

    public static DateTime RestoreUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Application writes reject non-UTC values, and SQLite drops DateTime.Kind.
            DateTimeKind.Unspecified => DateTime.SpecifyKind(
                value,
                DateTimeKind.Utc),
            _ => throw new InvalidOperationException(
                "The physical identity observation has an unsupported DateTime kind.")
        };
}
