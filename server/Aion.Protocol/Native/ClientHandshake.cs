using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Aion.Protocol.Native;

/// <summary>Client side of the native opening exchange (RSA greeting and RC4 key unwrap).</summary>
public sealed class ClientRsaKey
{
    public BigInteger Modulus { get; }
    public BigInteger PublicExponent { get; }
    public BigInteger PrivateExponent { get; }

    ClientRsaKey(BigInteger modulus, BigInteger publicExponent, BigInteger privateExponent)
    {
        Modulus = modulus;
        PublicExponent = publicExponent;
        PrivateExponent = privateExponent;
    }

    public static ClientRsaKey Generate(int seed = 0)
    {
        var rng = seed == 0 ? RandomNumberGenerator.Create() : new DeterministicRng(seed);
        while (true)
        {
            var p = ProbablePrime(512, rng);
            var q = ProbablePrime(512, rng);
            if (p == q) continue;
            var n = p * q;
            const int e = 65537;
            var phi = (p - 1) * (q - 1);
            if (phi % e == 0) continue;
            var d = ModInverse(e, phi);
            return new ClientRsaKey(n, e, d);
        }
    }

    public byte[] LoginGreeting() => Greeting(NativeHandshake.LoginGreeting);
    public byte[] GameGreeting() => Greeting(NativeHandshake.GameGreeting);

    byte[] Greeting(ushort tag)
    {
        var der = DerPublicKey();
        var trailer = tag == NativeHandshake.LoginGreeting ? 11 : 11;
        return new PacketWriter(der.Length + 32).U16(tag).Varint(der.Length).Raw(der).Zeros(trailer).ToArray();
    }

    byte[] DerPublicKey()
    {
        static byte[] Integer(BigInteger v)
        {
            var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (raw.Length > 0 && raw[0] >= 0x80) raw = [0x00, .. raw];
            return Tlv(0x02, raw);
        }
        return Tlv(0x30, Integer(Modulus).Concat(Integer(PublicExponent)).ToArray());
    }

    static byte[] Tlv(int tag, byte[] value)
    {
        var length = value.Length;
        byte[] head = length switch
        {
            < 128 => [(byte)length],
            < 256 => [0x81, (byte)length],
            _ => [0x82, (byte)(length >> 8), (byte)length],
        };
        return [(byte)tag, .. head, .. value];
    }

    /// <summary>RC4 session key from a deframed 0x3901 or 0x3611 body.</summary>
    public byte[] UnwrapSessionKey(ReadOnlySpan<byte> replyBody)
    {
        var reader = new PacketReader(replyBody.ToArray());
        _ = reader.U16(); // opcode
        _ = reader.U16(); // result
        _ = reader.U16(); // parameter
        if (replyBody[0] == 0x11) _ = reader.U16(); // game reply extra u16
        var cipher = reader.Bytes();
        var encoded = BigInteger.ModPow(new BigInteger(cipher, isUnsigned: true, isBigEndian: true), PrivateExponent, Modulus)
            .ToByteArray(isUnsigned: true, isBigEndian: true);
        var size = (Modulus.GetBitLength() + 7) / 8;
        var block = new byte[size];
        encoded.CopyTo(block, size - encoded.Length);
        return OaepDecode(block);
    }

    static byte[] OaepDecode(ReadOnlySpan<byte> block)
    {
        const int hlen = 20;
        if (block.Length < 2 * hlen + 2 || block[0] != 0) throw new ProtocolException("Invalid OAEP block");
        var maskedSeed = block.Slice(1, hlen);
        var maskedDb = block.Slice(1 + hlen);
        var seed = Xor(maskedSeed, Mgf1(maskedDb, hlen));
        var db = Xor(maskedDb, Mgf1(seed, maskedDb.Length));
        var digest = SHA1.HashData(ReadOnlySpan<byte>.Empty);
        if (!db.AsSpan(0, hlen).SequenceEqual(digest)) throw new ProtocolException("OAEP label mismatch");
        var index = db.AsSpan(hlen).IndexOf((byte)1);
        if (index < 0) throw new ProtocolException("OAEP delimiter missing");
        return db.AsSpan(hlen + index + 1).ToArray();
    }

    static byte[] Mgf1(ReadOnlySpan<byte> seed, int length)
    {
        var output = new byte[length];
        var input = new byte[seed.Length + 4];
        seed.CopyTo(input);
        for (int counter = 0, written = 0; written < length; counter++)
        {
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(seed.Length), counter);
            var digest = SHA1.HashData(input);
            var count = Math.Min(digest.Length, length - written);
            digest.AsSpan(0, count).CopyTo(output.AsSpan(written));
            written += count;
        }
        return output;
    }

    static byte[] Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var output = new byte[a.Length];
        for (var i = 0; i < a.Length; i++) output[i] = (byte)(a[i] ^ b[i]);
        return output;
    }

    static BigInteger ProbablePrime(int bits, RandomNumberGenerator rng)
    {
        var bytes = new byte[(bits + 7) / 8];
        while (true)
        {
            rng.GetBytes(bytes);
            bytes[^1] |= 1;
            bytes[0] |= 0x80;
            var candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            candidate |= BigInteger.One << (bits - 1);
            candidate |= BigInteger.One << (bits - 2);
            if (candidate.IsEven) continue;
            if (SmallPrimeCheck(candidate) && MillerRabin(candidate, rng)) return candidate;
        }
    }

    static bool SmallPrimeCheck(BigInteger n)
    {
        int[] primes = [3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37];
        foreach (var p in primes)
            if (n % p == 0) return false;
        return true;
    }

    static bool MillerRabin(BigInteger n, RandomNumberGenerator rng, int rounds = 12)
    {
        var d = n - 1;
        var s = 0;
        while (d.IsEven) { d >>= 1; s++; }
        var bytes = new byte[n.GetByteCount()];
        for (var round = 0; round < rounds; round++)
        {
            rng.GetBytes(bytes);
            var a = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) % (n - 2) + 2;
            var x = BigInteger.ModPow(a, d, n);
            if (x == 1 || x == n - 1) continue;
            var composite = true;
            for (var r = 0; r < s - 1; r++)
            {
                x = BigInteger.ModPow(x, 2, n);
                if (x == n - 1) { composite = false; break; }
            }
            if (composite) return false;
        }
        return true;
    }

    static BigInteger ModInverse(BigInteger e, BigInteger m)
    {
        var (t, newT) = (BigInteger.Zero, BigInteger.One);
        var (r, newR) = (m, e);
        while (newR != 0)
        {
            var q = r / newR;
            (t, newT) = (newT, t - q * newT);
            (r, newR) = (newR, r - q * newR);
        }
        if (r > 1) throw new InvalidOperationException("Modulus not invertible");
        if (t < 0) t += m;
        return t;
    }

    sealed class DeterministicRng(int seed) : RandomNumberGenerator
    {
        readonly Random random = new(seed);
        public override void GetBytes(byte[] data)
        {
            random.NextBytes(data);
        }
    }
}