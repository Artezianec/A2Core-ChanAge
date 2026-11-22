using Aion.Host.Native;
using Xunit;

namespace Aion.Host.Tests;

public sealed class QaBotServiceBackoffTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(5, 800)]
    [InlineData(20, 51_200)]
    public void Failure_backoff_caps_at_one_minute(int failures, int expectedMs) =>
        Assert.Equal(expectedMs, QaBotService.FailureBackoffMs(failures));
}