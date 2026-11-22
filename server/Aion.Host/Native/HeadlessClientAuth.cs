using System.Net;
using Aion.Protocol.Native;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>Headless-client grants when <see cref="HeadlessClientOptions"/> is enabled.</summary>
public sealed class HeadlessClientAuth(HeadlessClientOptions options)
{
    public bool TryLoginGrant(ClientIdentity identity, IPAddress? address, out LoginGrant? grant)
    {
        grant = null;
        if (!options.Allows(address) || !options.AcceptsToken(identity.AuthnToken)) return false;
        var wire = options.WireAccountId(identity.AuthnToken);
        grant = new LoginGrant(wire, Guid.NewGuid(), []);
        return true;
    }

    public bool TryGameSession(ClientIdentity identity, IPAddress? address)
        => options.Allows(address) && options.AcceptsToken(identity.AuthnToken);
}