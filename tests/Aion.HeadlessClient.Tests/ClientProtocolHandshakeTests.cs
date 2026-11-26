using Aion.HeadlessClient.Protocol;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.HeadlessClient.Tests;

public sealed class ClientProtocolHandshakeTests
{
    [Fact]
    public void Legacy_game_handshake_reply_opcode_unchanged()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var rsa = ClientRsaKey.Generate(1);
        var server = new RsaPublicKey(rsa.Modulus, rsa.PublicExponent);
        var framed = NativeHandshake.GameReply(server, key);
        var body = NativeFraming.Split(framed, out _)[0];
        Assert.Equal(0x3611, body[0] | body[1] << 8);
    }

    [Fact]
    public void Modern_game_handshake_reply_uses_CB02_for_2_0_12()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var rsa = ClientRsaKey.Generate(2);
        var server = new RsaPublicKey(rsa.Modulus, rsa.PublicExponent);
        var framed = NativeHandshake.WorldReply(server, key, v13: false);
        var body = NativeFraming.Split(framed, out _)[0];
        Assert.Equal(0xCB02, body[0] | body[1] << 8);
    }

    [Fact]
    public void Client_frame_backlog_legacy_always_decrypts()
    {
        var cipherKey = new byte[] { 1, 2, 3, 4 };
        var cipher = new Rc4(cipherKey);
        var frame = PacketWriter.Opcode(0x3602).U64(42).ToArray();
        var encrypted = cipher.Crypt(frame);
        var backlog = new ClientFrameBacklog(new Rc4(cipherKey), detectClearOpcodes: false);
        backlog.Add([encrypted]);
        var plain = backlog.Take(1).Single();
        Assert.Equal(0x3602, plain[0] | plain[1] << 8);
    }

    [Fact]
    public void Client_frame_backlog_2012_leaves_clear_opcode_plain()
    {
        var cipherKey = new byte[] { 9, 8, 7 };
        var frame = PacketWriter.Opcode(0xD456).I32(0).ToArray();
        var backlog = new ClientFrameBacklog(new Rc4(cipherKey), detectClearOpcodes: true);
        backlog.Add([frame]);
        var plain = backlog.Take(1).Single();
        Assert.Equal(frame, plain);
    }
}