using Aion.Host.NcGuard;
using Xunit;

namespace Aion.Host.Tests;

public sealed class NcGuardStopFileTests
{
    [Fact]
    public void ClearStale_deletes_file_older_than_process_start()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ncguard-stop-{Guid.NewGuid():N}");
        File.WriteAllText(path, "stop");
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
            var started = DateTimeOffset.UtcNow;
            Assert.True(NcGuardStopFile.ClearStale(path, started));
            Assert.False(File.Exists(path));
            Assert.False(NcGuardStopFile.IsRequested(path, started));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ClearStale_keeps_file_written_after_process_start()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ncguard-stop-{Guid.NewGuid():N}");
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);
        File.WriteAllText(path, "stop");
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            Assert.False(NcGuardStopFile.ClearStale(path, started));
            Assert.True(File.Exists(path));
            Assert.True(NcGuardStopFile.IsRequested(path, started));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_or_null_path_is_not_a_stop_request()
    {
        var started = DateTimeOffset.UtcNow;
        var missing = Path.Combine(Path.GetTempPath(), $"ncguard-missing-{Guid.NewGuid():N}");
        Assert.False(NcGuardStopFile.ClearStale(null, started));
        Assert.False(NcGuardStopFile.ClearStale("", started));
        Assert.False(NcGuardStopFile.ClearStale(missing, started));
        Assert.False(NcGuardStopFile.IsRequested(null, started));
        Assert.False(NcGuardStopFile.IsRequested(missing, started));
    }
}
