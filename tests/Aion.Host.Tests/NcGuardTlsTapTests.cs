using Aion.Host.NcGuard;
using Xunit;

namespace Aion.Host.Tests;

public sealed class NcGuardTlsTapTests
{
    [Theory]
    [InlineData("GET / HTTP/1.1\r\n", true)]
    [InlineData("POST / HTTP/1.1\r\n", true)]
    [InlineData("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", true)]
    [InlineData("\x01\x02\x03\x04", false)]
    public void LooksLikeHttp_classifies_preface(string text, bool expected)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, NcGuardTlsTapMiddleware.LooksLikeHttp(bytes));
    }
}