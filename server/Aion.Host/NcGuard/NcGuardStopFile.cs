namespace Aion.Host.NcGuard;

/// <summary>
/// The elevated NCGuard host is stopped by touching a stop file, because a
/// non-elevated shell cannot kill it. That file must not survive into the next
/// start, or the new host exits immediately and port 443 never opens.
/// </summary>
public static class NcGuardStopFile
{
    /// <summary>
    /// Deletes a leftover stop file written before <paramref name="processStartedAt"/>.
    /// A file written at or after that instant is left alone (a live stop request).
    /// </summary>
    /// <returns>True when a stale file was removed.</returns>
    public static bool ClearStale(string? path, DateTimeOffset processStartedAt)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        if (!IsStale(path, processStartedAt))
            return false;
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when a live (not leftover) stop file is present.</summary>
    public static bool IsRequested(string? path, DateTimeOffset processStartedAt)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        return !IsStale(path, processStartedAt);
    }

    internal static DateTimeOffset ProcessStartedAt()
    {
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        if (started.Kind == DateTimeKind.Unspecified)
            started = DateTime.SpecifyKind(started, DateTimeKind.Local);
        return started.ToUniversalTime();
    }

    static bool IsStale(string path, DateTimeOffset processStartedAt)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) < processStartedAt.UtcDateTime;
        }
        catch (IOException)
        {
            // Unreadable leftover: treat as stale so it cannot kill a fresh host.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
