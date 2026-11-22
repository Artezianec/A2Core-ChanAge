using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Applies outbound map-load packets (0x3621) with 0x3622, shared by live QA bots and headless tests.</summary>
public static class QaBotFeed
{
    public static async Task ApplyThinkAsync(GameSession session, QaThinkResult think, long now, CancellationToken ct = default)
    {
        await ApplyOutboundAsync(session, think.Outbound, now, ct);
        foreach (var request in think.Requests)
            await ApplyClientRequestAsync(session, request, now, ct);
    }

    /// <summary>Server-issued move/load packets (0x3621, 0x3647, …) must be fed with 0x3622, not sent as client requests.</summary>
    public static bool IsQaOutboundPacket(byte[] packet)
    {
        if (packet.Length < 2) return false;
        var op = (ushort)(packet[0] | (packet[1] << 8));
        return op is 0x3621 or 0x3647 or 0x3635 or 0x3642;
    }

    public static (List<byte[]> Outbound, List<byte[]> Client) PartitionPackets(IReadOnlyList<byte[]> packets)
    {
        var outbound = new List<byte[]>();
        var client = new List<byte[]>();
        foreach (var packet in packets)
        {
            if (IsQaOutboundPacket(packet)) outbound.Add(packet);
            else client.Add(packet);
        }
        return (outbound, client);
    }

    public static async Task ApplyClientRequestAsync(GameSession session, byte[] request, long now, CancellationToken ct = default)
    {
        if (IsQaOutboundPacket(request))
        {
            await ApplyOutboundAsync(session, [request], now, ct);
            return;
        }
        var replies = await session.HandleAsync(request, now, ct);
        await ApplyOutboundAsync(session, replies, now, ct);
    }

    public static async Task ApplyOutboundAsync(GameSession session, IReadOnlyList<byte[]> packets, long now, CancellationToken ct = default)
    {
        if (packets.Count == 0) return;
        var loads = new Queue<int>();
        EnqueueLoadTickets(packets, loads);
        while (loads.Count > 0)
        {
            var ticket = loads.Dequeue();
            var loaded = await session.HandleAsync(BotRequests.LoadComplete(ticket), now, ct);
            EnqueueLoadTickets(loaded, loads);
        }
    }

    internal static void EnqueueLoadTickets(IReadOnlyList<byte[]> packets, Queue<int> loads)
    {
        foreach (var p in packets)
        {
            if (p.Length >= 6 && p[0] == 0x21 && p[1] == 0x36)
                loads.Enqueue(BitConverter.ToInt32(p, 2));
        }
    }
}