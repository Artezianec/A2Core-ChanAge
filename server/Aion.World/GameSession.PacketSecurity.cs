using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    readonly ClientPacketLimiter packetLimiter = new();
    public int PacketRateDrops => packetLimiter.Dropped;
    public int PacketFloodBursts => packetLimiter.FloodBursts;

    /// <summary>Set by the host to log dead-state unanswered opcodes and disconnect traces.</summary>
    public Action<string>? ConnectionLog { get; set; }

    bool AcceptClientPacket(ushort opcode, long now) => packetLimiter.Allow(opcode, now);

    public void RecordPacketFrameBurst(int frames, int maxFrames) => packetLimiter.RecordFrameBurst(frames, maxFrames);

    static bool IsDeathHandledRequest(ushort opcode) =>
        opcode is RidePackets.RespawnRq or RidePackets.UseRebirthRq or 0x3622 or 0x3630;

    /// <summary>Malformed bodies must not escape the session loop.</summary>
    public async Task<List<byte[]>> HandleClientPacketSafeAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 2) return [];
        var opcode = Opcode(plain);
        LogGamePacket(outbound: false, plain, now);
        PacketTrace.RecordClient(opcode, plain.Length, now);
        if (!AcceptClientPacket(opcode, now)) return [];
        try
        {
            var replies = await HandleAsync(plain, now, ct);
            if (Dead && replies.Count == 0 && !SilentRequests.Contains(opcode) && PositionReport(plain) is null && !IsDeathHandledRequest(opcode))
                ConnectionLog?.Invoke($"dead-state unanswered opcode 0x{opcode:x4} ({plain.Length} bytes)");
            return replies;
        }
        catch (Exception error) when (error is ProtocolException or ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        {
            if (Dead)
                ConnectionLog?.Invoke($"dead-state opcode 0x{opcode:x4} rejected: {error.GetType().Name}");
            return [];
        }
    }

    static readonly HashSet<ushort> WireTraceOpcodes = ParseWireTraceOpcodes(Environment.GetEnvironmentVariable("AION_WIRE_TRACE_OPCODES"));

    static HashSet<ushort> ParseWireTraceOpcodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var set = new HashSet<ushort>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var opcode))
                set.Add(opcode);
        }
        return set;
    }

    public void RecordOutboundPackets(IEnumerable<byte[]> packets, long nowMs)
    {
        foreach (var packet in packets)
        {
            if (packet.Length < 2) continue;
            LogGamePacket(outbound: true, packet, nowMs);
            if (WireTraceOpcodes.Count > 0)
            {
                var opcode = Opcode(packet);
                if (WireTraceOpcodes.Contains(opcode))
                    ConnectionLog?.Invoke($"wire-out 0x{opcode:x4} len={packet.Length} body={Convert.ToHexString(packet)}");
            }
            PacketTrace.RecordServer(Opcode(packet), packet.Length, nowMs);
        }
    }

    void LogGamePacket(bool outbound, byte[] body, long nowMs)
    {
        if (PlaytestDebug.Sink is not { Packets: true } sink || body.Length < 2) return;
        sink.Packet(new PlaytestPacketRecord("game", outbound, Opcode(body), body, body.Length,
            CharacterName is null ? null : playerKey, nowMs));
    }
}
