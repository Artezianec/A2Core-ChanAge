using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Aion.Host.NcGuard;

internal static class NcGuardTlsFeatureAccess
{
    internal static string AlpnText(IFeatureCollection features)
    {
        try
        {
            var alpnProto = features.Get<ITlsApplicationProtocolFeature>()?.ApplicationProtocol;
            return alpnProto is { Length: > 0 } p ? Encoding.UTF8.GetString(p.Span) : "(none)";
        }
        catch (Exception error) when (error is ObjectDisposedException or IOException)
        {
            return "(none)";
        }
    }

    internal static string HttpProtocol(IFeatureCollection features)
    {
        try
        {
            return features.Get<IHttpRequestFeature>()?.Protocol ?? "(none)";
        }
        catch (Exception error) when (error is ObjectDisposedException or IOException)
        {
            return "(none)";
        }
    }

    internal static bool IsBenignConnectionAbort(Exception error) =>
        error is ObjectDisposedException or IOException or OperationCanceledException;
}