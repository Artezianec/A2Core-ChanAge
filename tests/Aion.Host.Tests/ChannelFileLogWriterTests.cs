using Aion.Host.Logging;
using Xunit;

namespace Aion.Host.Tests;

public sealed class ChannelFileLogWriterTests
{
    [Fact]
    public async Task Writer_flushes_before_shutdown_without_close()
    {
        var path = Path.Combine(Path.GetTempPath(), "a2core-srv-" + Guid.NewGuid().ToString("N") + ".log");
        var writer = new ChannelFileLogWriter(path, capacity: 16);
        writer.Enqueue("hello from periodic flush test");
        await Task.Delay(3500);
        Assert.True(new FileInfo(writer.Path).Length > 0);
        var text = await File.ReadAllTextAsync(writer.Path);
        Assert.Contains("hello from periodic flush test", text);
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task Writer_stays_alive_across_idle_flush_cycles()
    {
        var path = Path.Combine(Path.GetTempPath(), "a2core-srv-" + Guid.NewGuid().ToString("N") + ".log");
        await using var writer = new ChannelFileLogWriter(path, capacity: 16);
        writer.Enqueue("first line");
        await Task.Delay(3500);
        Assert.True(new FileInfo(writer.Path).Length > 0);
        writer.Enqueue("second line after idle");
        await Task.Delay(3500);
        Assert.Null(writer.LastError);
        var text = await File.ReadAllTextAsync(writer.Path);
        Assert.Contains("first line", text);
        Assert.Contains("second line after idle", text);
    }
}