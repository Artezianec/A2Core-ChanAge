namespace Aion.Protocol.Native;

/// <summary>Canonical unsigned 32-bit LEB128, as on the native ports.</summary>
public static class NativeVarint
{
    public static uint Read(ReadOnlySpan<byte> data, int offset, out int next)
    {
        ulong value = 0;
        var start = offset;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= data.Length) throw new IncompleteException("Incomplete unsigned varint");
            var b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if (value > uint.MaxValue) throw new ProtocolException("Varint exceeds uint32");
            if ((b & 0x80) == 0)
            {
                var used = offset - start;
                if (used > 1 && value < 1UL << (7 * (used - 1))) throw new ProtocolException("Noncanonical varint");
                next = offset;
                return (uint)value;
            }
        }
        throw new ProtocolException("Varint exceeds five bytes");
    }

    public static byte[] Encode(long value) => new PacketWriter(8).Varint(value).ToArray();
}

/// <summary>
/// Native framing: a plaintext varint logical length (body length + 4), then the body.
/// Only bodies are encrypted; the prefix never advances the RC4 keystream.
/// </summary>
public static class NativeFraming
{
    public const int MaxFrameSize = 16 * 1024 * 1024;

    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) throw new ArgumentException("Body must be non-empty", nameof(body));
        return new PacketWriter(body.Length + 8).Varint(body.Length + 4).Raw(body).ToArray();
    }

    /// <summary>Complete frame bodies at the start of <paramref name="data"/>; <paramref name="consumed"/> excludes a partial tail.</summary>
    public static List<byte[]> Split(ReadOnlySpan<byte> data, out int consumed)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            uint logical;
            int bodyStart;
            try { logical = NativeVarint.Read(data, offset, out bodyStart); }
            catch (IncompleteException) { break; }
            if (logical < 6 || logical > MaxFrameSize) throw new ProtocolException("Logical frame length outside supported bounds");
            var end = bodyStart + (int)logical - 4;
            if (end > data.Length) break;
            frames.Add(data[bodyStart..end].ToArray());
            offset = end;
            if (frames.Count > 16384) throw new ProtocolException("Frame count limit exceeded");
        }
        consumed = offset;
        return frames;
    }

    /// <summary>0xffff envelope: u32 inner length, then an LZ4 block of the framed body (used for 0x3615).</summary>
    public static byte[] CompressedEnvelope(ReadOnlySpan<byte> body)
    {
        var inner = Frame(body);
        return new PacketWriter(inner.Length + 16).U16(0xffff).U32((uint)inner.Length).Raw(Lz4Block.Encode(inner)).ToArray();
    }

    /// <summary>
    /// 0xffff bundle of several frames: the official 2.0.13 world-entry state arrives as one LZ4 bundle whose inner
    /// stream holds many framed packets (capture 2026-11-25 13:46:47, 9.444 s).
    /// </summary>
    public static byte[] Bundle(IReadOnlyList<byte[]> bodies)
    {
        if (bodies.Count == 0) throw new ArgumentException("Bundle needs at least one body", nameof(bodies));
        var size = 0;
        foreach (var body in bodies) size += body.Length + 8;
        var inner = new byte[size];
        var at = 0;
        foreach (var body in bodies)
        {
            var framed = Frame(body);
            framed.CopyTo(inner, at);
            at += framed.Length;
        }
        if (at != size) Array.Resize(ref inner, at);
        return new PacketWriter(inner.Length + 16).U16(0xffff).U32((uint)inner.Length).Raw(Lz4Block.Encode(inner)).ToArray();
    }
}

/// <summary>Growable receive buffer that yields complete frames and keeps the partial tail.</summary>
public sealed class FrameBuffer
{
    byte[] data = new byte[8192];
    int length;

    public int Length => length;

    /// <summary>The buffered bytes (a partial frame's head), for diagnostics.</summary>
    public ReadOnlySpan<byte> Snapshot() => data.AsSpan(0, length);

    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (length + chunk.Length > NativeFraming.MaxFrameSize + 16) throw new ProtocolException("Receive buffer limit exceeded");
        if (length + chunk.Length > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, length + chunk.Length));
        chunk.CopyTo(data.AsSpan(length));
        length += chunk.Length;
    }

    public List<byte[]> Drain()
    {
        var frames = NativeFraming.Split(data.AsSpan(0, length), out var consumed);
        data.AsSpan(consumed, length - consumed).CopyTo(data);
        length -= consumed;
        return frames;
    }
}

/// <summary>
/// Client frames waiting for the game loop. Every frame is deciphered as it arrives, in order, so the RC4 keystream stays in step
/// with the client; the loop then takes at most <c>perTick</c> of them per tick and the rest wait for the next one. Live 2026-11-12
/// (21:29:54) one TCP read held more than 64 frames: the loop dropped the extra ones without deciphering them, the keystream fell
/// behind, and every later request read as a random opcode (about 800 "unanswered opcodes", 10-byte movement among them) until the
/// client gave up a minute later. Only frames past <see cref="Capacity"/> are dropped, still deciphered.
/// </summary>
public sealed class ClientFrameBacklog(Rc4 cipher, int capacity = 1024)
{
    readonly Queue<byte[]> frames = new();
    bool? encrypted;

    public int Capacity { get; } = capacity;
    public int Count => frames.Count;

    /// <summary>
    /// 2.0.12's game port sends the opcode in clear and the payload unencrypted (the 2.0.6 client
    /// encrypted the whole body). The first frame decides for the connection: a known clear opcode
    /// (0xD456 world login, 0x3602 ping) turns the RC4 off; anything else keeps the legacy cipher.
    /// </summary>
    static bool ClearWorldOpcode(ReadOnlySpan<byte> body) =>
        body.Length >= 2 && (body[0] | body[1] << 8) is 0xD456 or 0x4BAF or 0x3602;

    /// <summary>Deciphers and queues the frames; returns how many were dropped because the backlog was full.</summary>
    public int Add(IEnumerable<byte[]> bodies)
    {
        var dropped = 0;
        foreach (var body in bodies)
        {
            encrypted ??= !ClearWorldOpcode(body);
            var plain = encrypted.Value ? cipher.Crypt(body) : body;
            if (frames.Count >= Capacity) dropped++;
            else frames.Enqueue(plain);
        }
        return dropped;
    }

    /// <summary>The next deciphered frames, at most <paramref name="max"/>.</summary>
    public List<byte[]> Take(int max)
    {
        var taken = new List<byte[]>(Math.Min(max, frames.Count));
        while (taken.Count < max && frames.TryDequeue(out var plain)) taken.Add(plain);
        return taken;
    }
}
