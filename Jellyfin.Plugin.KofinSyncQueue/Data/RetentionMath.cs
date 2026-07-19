namespace Jellyfin.Plugin.KofinSyncQueue.Data;

/// <summary>
/// Retention arithmetic, kept pure for tests.
/// </summary>
public static class RetentionMath
{
    private const long SecondsPerDay = 86400L;

    /// <summary>
    /// The retention cutoff as unix seconds; 0 when retention is disabled
    /// (keep forever — clients then know overrun is impossible).
    /// </summary>
    /// <param name="retentionDays">Configured days; 0 or negative disables.</param>
    /// <param name="nowUnixSeconds">The current server time.</param>
    /// <returns>The cutoff, or 0.</returns>
    public static long CutoffFor(int retentionDays, long nowUnixSeconds)
    {
        return retentionDays > 0 ? nowUnixSeconds - (retentionDays * SecondsPerDay) : 0;
    }
}
