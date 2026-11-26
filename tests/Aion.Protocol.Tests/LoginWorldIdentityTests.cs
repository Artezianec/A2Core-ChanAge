using Aion.Protocol.Native;
using Xunit;

namespace Aion.Protocol.Tests;

public sealed class LoginWorldIdentityTests
{
    [Fact]
    public void Legacy_login_identity_shape_unchanged()
    {
        var body = PacketWriter.Opcode(0x3905).String("").String("ticket-abc").String("x").String("").String("").String("").ToArray();
        var id = ClientIdentity.Parse(body, game: false, tryVarintNpLogin: false);
        Assert.Equal("ticket-abc", id.AuthnToken);
    }

    [Fact]
    public void Varint_np_login_not_used_when_disabled()
    {
        var body = PacketWriter.Opcode(0x3905).Varint(0).Varint(11).Raw("np"u8.ToArray()).ToArray();
        Assert.Throws<ProtocolException>(() => ClientIdentity.Parse(body, game: false, tryVarintNpLogin: false));
    }

    [Fact]
    public void Parse_world_accepts_2_0_12_D456_layout()
    {
        var grant = System.Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var session = $"42:{grant:D}";
        var body = PacketWriter.Opcode(0xD456)
            .Varint(session.Length).Raw(System.Text.Encoding.UTF8.GetBytes(session))
            .Varint(0).Varint(0)
            .I64(0)
            .Varint(6).Raw("authn1"u8.ToArray())
            .U8(0)
            .ToArray();
        var id = ClientIdentity.ParseWorld(body);
        Assert.Equal(session, id.AccountSession);
        Assert.Equal("authn1", id.AuthnToken);
    }
}