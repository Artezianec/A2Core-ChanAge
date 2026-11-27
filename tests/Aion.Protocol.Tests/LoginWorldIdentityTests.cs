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
    public void Modern_np_login_accepts_usmap_field_order_with_varint_lengths()
    {
        const string ticket = "a1b2c3d4e5f607182930415263748596a7b8c9d0e1f203142536475869708192a0b1c2d3";
        var body = PacketWriter.Opcode(0x3905)
            .String("steam").String(ticket).String("").String("platform-session").String("42").String("en-US")
            .ToArray();
        var id = ClientIdentity.Parse(body, game: false, tryVarintNpLogin: true);
        Assert.Equal(ticket, id.AuthnToken);
        Assert.Equal("platform-session", id.AccountSession);
    }

    [Fact]
    public void Modern_np_login_accepts_u16_length_strings()
    {
        const string ticket = "f1e2d3c4b5a69788796a594837261504f3e2d1c0b9a89786756453423120191817161514131211";
        var body = BuildU16LengthLogin("steam", ticket, "", "", "", "en");
        var id = ClientIdentity.Parse(body, game: false, tryVarintNpLogin: true);
        Assert.Equal(ticket, id.AuthnToken);
    }

    [Fact]
    public void TryExtractGameTicket_finds_embedded_hex_and_describes_shape()
    {
        const string ticket = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var wrapped = "platform:" + ticket + ":suffix";
        Assert.Equal(ticket, ClientIdentity.TryExtractGameTicket(wrapped));
        Assert.Equal("len=80 charset=utf8 prefix=platfo", ClientIdentity.DescribeCredentialShape(wrapped));
        Assert.Equal("len=64 charset=hex prefix=012345", ClientIdentity.DescribeCredentialShape(ticket));
    }

    [Fact]
    public void Modern_np_login_2_0_13_authn_token_is_third_string_not_device_hash_in_second()
    {
        const string deviceHash = "673b7aabcdef0123456789abcdef0123456789abcdef0123456789abcdef0123";
        const string ticket = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var body = PacketWriter.Opcode(0x3905)
            .String("steam").String(deviceHash).String(ticket).String("").String("").String("")
            .ToArray();
        var id = ClientIdentity.Parse(body, game: false, tryVarintNpLogin: true);
        Assert.Equal(ticket, id.AuthnToken);
        Assert.True(ClientIdentity.TryReadNpLoginFields(body, out var fields, out var authnIndex));
        Assert.Equal(2, authnIndex);
        Assert.Equal(deviceHash, fields[1]);
    }

    static byte[] BuildU16LengthLogin(params string[] fields)
    {
        var w = PacketWriter.Opcode(0x3905);
        foreach (var text in fields)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            w.U16(bytes.Length).Raw(bytes);
        }
        return w.ToArray();
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