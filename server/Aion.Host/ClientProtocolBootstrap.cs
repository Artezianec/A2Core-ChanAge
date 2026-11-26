using Aion.Core;
using Microsoft.Extensions.Configuration;

namespace Aion.Host;

static class ClientProtocolBootstrap
{
    public static ClientProtocolSettings Load(IConfiguration configuration)
    {
        var raw = Environment.GetEnvironmentVariable("AION_CLIENT_PROTOCOL");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = configuration["Aion:ClientProtocol"]?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
                raw = configuration["Aion:ClientBuild"]?.Trim();
        }
        var settings = ClientProtocolSettings.Parse(raw);
        ClientProtocolRuntime.Initialize(settings);
        return settings;
    }
}