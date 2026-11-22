using System.Text.Json;

namespace Aion.Protocol.Native;

/// <summary>Best-effort server-to-client opcode names and a few high-value parsers.</summary>
public sealed class ServerPacketDecoder
{
    readonly Dictionary<ushort, string> names;

    ServerPacketDecoder(Dictionary<ushort, string> names) => this.names = names;

    public static ServerPacketDecoder LoadFromFile(string path)
    {
        if (!File.Exists(path)) return new ServerPacketDecoder([]);
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var map = new Dictionary<ushort, string>();
        foreach (var entry in doc.RootElement.EnumerateObject())
            if (entry.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && ushort.TryParse(entry.Name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var opcode))
                map[opcode] = entry.Value.GetString() ?? entry.Name;
        return new ServerPacketDecoder(map);
    }

    public string Name(ushort opcode) => names.TryGetValue(opcode, out var name) ? name : $"0x{opcode:x4}";

    public bool TryParseOpcode(ReadOnlySpan<byte> body, out ushort opcode)
    {
        opcode = 0;
        if (body.Length < 2) return false;
        opcode = (ushort)(body[0] | body[1] << 8);
        return true;
    }

    /// <summary>0xffff LZ4 envelope to inner framed bodies.</summary>
    public IReadOnlyList<byte[]> UnwrapEnvelope(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6 || body[0] != 0xff || body[1] != 0xff) return [body.ToArray()];
        var innerSize = BitConverter.ToUInt32(body.Slice(2, 4));
        var inner = Lz4Block.Decode(body.Slice(6), checked((int)innerSize));
        return NativeFraming.Split(inner, out _).Select(b => b).ToList();
    }

    public ParsedServerPacket Parse(byte[] body)
    {
        if (!TryParseOpcode(body, out var opcode))
            return new ParsedServerPacket(0, "truncated", body, false, null);
        object? detail = opcode switch
        {
            0x3603 => ParsePing(body),
            0x3621 => ParsePrepareEnterMap(body),
            0x8d00 => ParseEntityPoints(body),
            0x8d11 => "quest_add",
            _ => null,
        };
        return new ParsedServerPacket(opcode, Name(opcode), body, detail is not null, detail);
    }

    static object ParsePing(byte[] body)
    {
        if (body.Length < 18) return "ping_short";
        return new { client = BitConverter.ToUInt64(body, 4), serverMs = BitConverter.ToInt64(body, 12) };
    }

    static object ParsePrepareEnterMap(byte[] body)
    {
        if (body.Length < 30) return "prepare_short";
        return new
        {
            ticket = BitConverter.ToInt32(body, 2),
            map = BitConverter.ToInt32(body, 6),
            x = BitConverter.ToSingle(body, 18),
            y = BitConverter.ToSingle(body, 22),
            z = BitConverter.ToSingle(body, 26),
        };
    }

    static object ParseEntityPoints(byte[] body)
    {
        try
        {
            var reader = new PacketReader(body) { Offset = 2 };
            var key = reader.Varint();
            return new { key };
        }
        catch (ProtocolException)
        {
            return "points_unparsed";
        }
    }
}

public sealed record ParsedServerPacket(ushort Opcode, string Name, byte[] Body, bool Parsed, object? Detail);