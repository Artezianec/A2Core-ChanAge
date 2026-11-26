using Aion.Host.NcGuard;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Aion.Host.Tests;

public sealed class NcGuardTlsFeatureAccessTests
{
    [Fact]
    public void AlpnText_returns_none_when_feature_throws_after_disconnect()
    {
        var features = new FeatureCollection();
        features.Set<ITlsApplicationProtocolFeature>(new DisposedAlpnFeature());
        Assert.Equal("(none)", NcGuardTlsFeatureAccess.AlpnText(features));
    }

    [Fact]
    public void HttpProtocol_returns_none_when_feature_throws_after_disconnect()
    {
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new DisposedHttpRequestFeature());
        Assert.Equal("(none)", NcGuardTlsFeatureAccess.HttpProtocol(features));
    }

    [Theory]
    [InlineData(typeof(ObjectDisposedException), true)]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(OperationCanceledException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void IsBenignConnectionAbort_classifies_early_disconnect(Type errorType, bool expected)
    {
        var error = (Exception)Activator.CreateInstance(errorType, "test")!;
        Assert.Equal(expected, NcGuardTlsFeatureAccess.IsBenignConnectionAbort(error));
    }

    sealed class DisposedAlpnFeature : ITlsApplicationProtocolFeature
    {
        public ReadOnlyMemory<byte> ApplicationProtocol => throw new ObjectDisposedException("SslStream");
    }

    sealed class DisposedHttpRequestFeature : IHttpRequestFeature
    {
        public string Protocol { get => throw new ObjectDisposedException("connection"); set { } }
        public string Method { get; set; } = "";
        public string Scheme { get; set; } = "";
        public string PathBase { get; set; } = "";
        public string Path { get; set; } = "";
        public string QueryString { get; set; } = "";
        public string RawTarget { get; set; } = "";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
    }
}