using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Aion.Protocol.Native;

/// <summary>RC4 with resumable state: one keystream continues over every client body.</summary>
public sealed class Rc4
{
    readonly byte[] state = new byte[256];
    int i, j;

    public Rc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty) throw new ArgumentException("Empty RC4 key", nameof(key));
        for (var n = 0; n < 256; n++) state[n] = (byte)n;
        for (int n = 0, k = 0; n < 256; n++)
        {
            k = (k + state[n] + key[n % key.Length]) & 0xFF;
            (state[n], state[k]) = (state[k], state[n]);
        }
    }

    public byte[] Crypt(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        for (var n = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + state[i]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
            output[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 0xFF]);
        }
        return output;
    }
}

/// <summary>The client's advertised RSA public key (2048-bit, e=3 observed) and OAEP-SHA1 encryption to it.</summary>
public enum RsaPadding { OaepSha1, OaepSha256, Pkcs1V15 }

public sealed record RsaPublicKey(BigInteger Modulus, BigInteger Exponent)
{
    public int Size => (int)((Modulus.GetBitLength() + 7) / 8);
    public long Bits => Modulus.GetBitLength();

    /// <summary>OAEP-SHA1 / MGF1-SHA1 / empty label (OpenSSL RSA_PKCS1_OAEP_PADDING), or the 2.0.13 probe variants.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> message, Func<int, byte[]>? seeds = null, RsaPadding padding = RsaPadding.OaepSha1)
    {
        seeds ??= RandomNumberGenerator.GetBytes;
        var plain = message.ToArray();
        byte[] Encode()
        {
            if (padding == RsaPadding.Pkcs1V15)
            {
                var em = new byte[Size];
                em[1] = 2;
                var padLength = Size - plain.Length - 3;
                if (padLength < 8) throw new ArgumentException("Message too long for PKCS#1 v1.5");
                var pad = seeds(padLength);
                for (var n = 0; n < padLength; n++) em[2 + n] = pad[n] == 0 ? (byte)1 : pad[n];
                plain.CopyTo(em.AsSpan(3 + padLength));
                return em;
            }
            var hlen = padding == RsaPadding.OaepSha256 ? 32 : 20;
            return OaepEncode(plain, Size, seeds(hlen), padding);
        }
        var encoded = Encode();
        if (new BigInteger(encoded, isUnsigned: true, isBigEndian: true) >= Modulus)
            encoded = Encode();  // one retry with a fresh seed
        var cipher = BigInteger.ModPow(new BigInteger(encoded, isUnsigned: true, isBigEndian: true), Exponent, Modulus);
        var bytes = cipher.ToByteArray(isUnsigned: true, isBigEndian: true);
        var output = new byte[Size];
        bytes.CopyTo(output, Size - bytes.Length);
        return output;
    }

    public static byte[] OaepEncode(ReadOnlySpan<byte> message, int size, ReadOnlySpan<byte> seed, RsaPadding padding = RsaPadding.OaepSha1)
    {
        var hlen = padding == RsaPadding.OaepSha256 ? 32 : 20;
        if (message.Length < 2 || message.Length > size - 2 * hlen - 2) throw new ArgumentException("Message length outside OAEP capacity");
        if (seed.Length != hlen) throw new ArgumentException("OAEP seed length does not match its hash");
        var db = new byte[size - hlen - 1];
        Hash(ReadOnlySpan<byte>.Empty, padding).CopyTo(db, 0);
        db[db.Length - message.Length - 1] = 1;
        message.CopyTo(db.AsSpan(db.Length - message.Length));
        var dbMask = Mgf1(seed, db.Length, padding);
        for (var n = 0; n < db.Length; n++) db[n] ^= dbMask[n];
        var seedMask = Mgf1(db, hlen, padding);
        var output = new byte[size];
        for (var n = 0; n < hlen; n++) output[1 + n] = (byte)(seed[n] ^ seedMask[n]);
        db.CopyTo(output, 1 + hlen);
        return output;
    }

    static byte[] Hash(ReadOnlySpan<byte> data, RsaPadding padding) =>
        padding == RsaPadding.OaepSha256 ? SHA256.HashData(data) : SHA1.HashData(data);

    static byte[] Mgf1(ReadOnlySpan<byte> seed, int length, RsaPadding padding)
    {
        var output = new byte[length];
        var input = new byte[seed.Length + 4];
        seed.CopyTo(input);
        for (int counter = 0, written = 0; written < length; counter++)
        {
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(seed.Length), counter);
            var digest = Hash(input, padding);
            var count = Math.Min(digest.Length, length - written);
            digest.AsSpan(0, count).CopyTo(output.AsSpan(written));
            written += count;
        }
        return output;
    }
}

/// <summary>
/// Opening exchange of both native ports. The client greets in plaintext (login 0x3900, game
/// 0x3610): u16 tag, varint DER length, DER RSAPublicKey, then a short trailer. The server
/// answers with a self-generated 32-byte RC4 key, RSA-OAEP encrypted to that key (login
/// 0x3901, game 0x3611); afterwards client bodies are RC4 and server bodies plaintext.
/// </summary>
public static class NativeHandshake
{
    public const ushort LoginGreeting = 0x3900, GameGreeting = 0x3610;
    /// <summary>
    /// Game-port greeting and its reply. 2.0.12 greets with 0x5E58 and is answered with 0xCB02 (Handshake_RS, the 2.0.12
    /// field order). The 2.0.13 client greets with 0x16FE and the official 2.0.13 server answers with 0x83A9 using the
    /// 2.0.13 declaration order (season, key, server, result, ping, client) — captured live 2026-11-25 13:04:28
    /// (`a2-20261125-130428.a2p` frame 14, 278 bytes, byte-exact).
    /// </summary>
    public const ushort WorldGreeting = 0x5E58, WorldGreetingV13 = 0x16FE,
        WorldReplyOpcode = 0xCB02, WorldReplyOpcodeV13 = 0x83A9;

    public static bool IsWorldGreeting(ReadOnlySpan<byte> body) =>
        body.Length >= 2 && (body[0] | body[1] << 8) is WorldGreeting or WorldGreetingV13;

    public static RsaPublicKey ReadGreeting(byte[] body, ushort expectedTag)
    {
        var reader = new PacketReader(body);
        if (reader.U16() != expectedTag) throw new ProtocolException($"First frame is not a 0x{expectedTag:x4} greeting");
        var der = reader.Take(checked((int)reader.Varint())).ToArray();
        return ParsePublicKey(der);
    }

    /// <summary>
    /// Game greeting (0x5E58). 2.0.12: country string, i32 version, u8 platform, u8 language, blob DER key,
    /// u8 language-pack flag, u8 shop, u8 binary. 2.0.13 reorders it (projectlumiel 2.0.13 usmap / structs-2.0.13):
    /// u8 language-pack flag, blob DER key, u8 binary, u8 language, u8 shop, u8 platform, country string, i32 version.
    /// <paramref name="v13"/> reports which layout the greeting used so the reply matches.
    /// </summary>
    public static RsaPublicKey ReadWorldGreeting(byte[] body) => ReadWorldGreeting(body, out _);

    public static RsaPublicKey ReadWorldGreeting(byte[] body, out bool v13)
    {
        var reader = new PacketReader(body);
        var tag = reader.U16();
        if (tag == WorldGreetingV13)
        {
            // 2.0.13 magic: the body is the 2.0.13 Handshake_RQ layout only.
            _ = reader.U8();                          // unsupported language pack
            var derMagic = reader.Bytes();
            _ = reader.U8(); _ = reader.U8(); _ = reader.U8(); _ = reader.U8();
            _ = ReadVarintString(reader);             // country
            _ = reader.I32();                         // version
            v13 = true;
            return ParsePublicKey(derMagic);
        }
        if (tag != WorldGreeting) throw new ProtocolException($"First frame is not a 0x{WorldGreeting:x4} or 0x{WorldGreetingV13:x4} greeting");
        var start = reader.Offset;
        try
        {
            _ = reader.U8();                          // unsupported language pack
            var der13 = reader.Bytes();               // varint blob
            _ = reader.U8(); _ = reader.U8(); _ = reader.U8(); _ = reader.U8();
            _ = ReadVarintString(reader);             // country
            _ = reader.I32();                         // version
            v13 = true;
            return ParsePublicKey(der13);
        }
        catch (ProtocolException)
        {
            v13 = false;
            reader.Offset = start;
        }
        _ = ReadVarintString(reader);                 // user agent country
        _ = reader.I32();                              // version
        _ = reader.U8();                               // platform
        _ = reader.U8();                               // language
        var der = reader.Bytes();                      // varint blob
        _ = reader.U8(); _ = reader.U8(); _ = reader.U8();
        return ParsePublicKey(der);
    }

    static RsaPublicKey ParsePublicKey(byte[] der)
    {
        var (tag, sequence, _) = DerTlv(der, 0);
        if (tag != 0x30) throw new ProtocolException("Not a DER sequence");
        var values = new List<BigInteger>();
        for (var offset = 0; offset < sequence.Length;)
        {
            (tag, var value, offset) = DerTlv(sequence, offset);
            if (tag != 2) throw new ProtocolException("Expected DER integer");
            values.Add(new BigInteger(value, isUnsigned: true, isBigEndian: true));
        }
        if (values.Count != 2) throw new ProtocolException("Expected modulus and exponent");
        if (values[0].GetBitLength() is < 1024 or > 8192 || values[1] < 3) throw new ProtocolException("Unsupported RSA public key");
        return new RsaPublicKey(values[0], values[1]);
    }

    static string ReadVarintString(PacketReader reader)
    {
        var length = checked((int)reader.Varint());
        return Encoding.UTF8.GetString(reader.Take(length));
    }

    static (int Tag, byte[] Value, int Next) DerTlv(byte[] data, int offset)
    {
        if (offset + 2 > data.Length) throw new ProtocolException("Truncated DER header");
        int tag = data[offset], length = data[offset + 1];
        offset += 2;
        if ((length & 0x80) != 0)
        {
            var width = length & 0x7F;
            if (width is < 1 or > 4 || offset + width > data.Length) throw new ProtocolException("Invalid DER length");
            if (data[offset] == 0) throw new ProtocolException("Noncanonical DER length");
            length = 0;
            for (var n = 0; n < width; n++) length = length << 8 | data[offset + n];
            offset += width;
            if (length < 128) throw new ProtocolException("Noncanonical DER length");
        }
        if (length < 0 || offset + length > data.Length) throw new ProtocolException("Truncated DER value");
        return (tag, data.AsSpan(offset, length).ToArray(), offset + length);
    }

    /// <summary>Framed 0x3901: result 0, parameter 60, varint cipher, 10 zero trailer bytes.</summary>
    public static byte[] LoginReply(RsaPublicKey key, ReadOnlySpan<byte> sessionKey, Func<int, byte[]>? seeds = null) =>
        NativeFraming.Frame(PacketWriter.Opcode(0x3901).U16(0).U16(60).Bytes(key.Encrypt(sessionKey, seeds)).Zeros(10).ToArray());

    /// <summary>Framed 0x3611: result 0, parameter 60000, one extra u16, varint cipher, 12 zero bytes.</summary>
    public static byte[] GameReply(RsaPublicKey key, ReadOnlySpan<byte> sessionKey, Func<int, byte[]>? seeds = null) =>
        NativeFraming.Frame(PacketWriter.Opcode(0x3611).U16(0).U16(60000).U16(0).Bytes(key.Encrypt(sessionKey, seeds)).Zeros(12).ToArray());

    /// <summary>
    /// Framed 0xCB02 (2.0.12 game): ping 60000, result 0, client version 0, season offset -17,
    /// server version 0, then the 256-byte RSA-OAEP cipher of the RC4 key as a varint blob
    /// (projectlumiel GameServer_Handshake_RS, byte-verified 2026-11-18).
    /// </summary>
    public static byte[] WorldReply(RsaPublicKey key, ReadOnlySpan<byte> sessionKey, Func<int, byte[]>? seeds = null, bool v13 = false,
        ushort? replyOpcode = null, RsaPadding padding = RsaPadding.OaepSha1) =>
        v13
            // 2.0.13 order (official capture 2026-11-25 13:04:28, frame 14, 278 bytes): season offset 0, key, server
            // version, result (i16 on the wire, as in 2.0.12), ping threshold 60000, client version.
            ? NativeFraming.Frame(PacketWriter.Opcode(replyOpcode ?? WorldReplyOpcodeV13).I32(0).Bytes(key.Encrypt(sessionKey, seeds, padding))
                .I32(0).U16(0).I32(60000).I32(0).ToArray())
            // 2.0.12 order (byte-verified 2026-11-18): ping, result, client version, season offset, server version, key.
            : NativeFraming.Frame(PacketWriter.Opcode(replyOpcode ?? WorldReplyOpcode).I32(60000).U16(0).I32(0).I32(-17).I32(0)
                .Bytes(key.Encrypt(sessionKey, seeds, padding)).ToArray());
}
