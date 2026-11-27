using Aion.Protocol.Native;

namespace Aion.World;

// 2.0.13-only packets that the 2.0.9 tables lack (ModernPackets). Everything here is gated to
// WireContext.Current == V213 so a 2.0.9 or 2.0.12 connection keeps its previous traffic.
public sealed partial class GameSession
{
    public const ushort CombatStateOnRq = ModernPackets.CombatStateOnRq, CombatStateOnRs = ModernPackets.CombatStateOnRs,
        CombatStateOffRq = ModernPackets.CombatStateOffRq, CombatStateOffRs = ModernPackets.CombatStateOffRs,
        ClientKeyBindingInfoRt = ModernPackets.ClientKeyBindingInfoRt;

    /// <summary>The last ClientKeyBindingInfo_RT key lists (main, sub); 2.0.13 only, no reply.</summary>
    public string? ClientMainKeyBindings { get; private set; }
    public string? ClientSubKeyBindings { get; private set; }

    /// <summary>Kina acquired this session (0xFFA6's _acquired_kina total).</summary>
    long acquiredKina;

    static bool IsV213 => WireContext.Current == WireVersion.V213;

    /// <summary>
    /// The 2.0.13 entry extras OBSERVED around the select/load burst: Kina and gather day limits,
    /// the account currency limit table, the reward-cube ceiling, the guild craft recipe list and
    /// the post notices. Empty on 2.0.9/2.0.12.
    /// </summary>
    List<byte[]> ModernEntryPackets(long now)
    {
        if (!IsV213) return [];
        var gatherCount = options.World?.GatherLimiter.Used(now) ?? 0;
        return
        [
            ModernPackets.InitKinaLimitStatus(acquiredKina, now),
            ModernPackets.InitGatherLimitStatus(gatherCount, now),
            ModernPackets.AccountCurrencyLimitStatus(now),
            ModernPackets.OdEnergyCubeCeilingInfo([]),
            ModernPackets.GuildCraftRecipeList(),
            ModernPackets.RetrievePostNoticeInfo(),
        ];
    }

    /// <summary>0xFFB3 ClientKeyBindingInfo_RT: two strings, stored on the session, never answered.</summary>
    void StoreClientKeyBindingInfo(byte[] plain)
    {
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            ClientMainKeyBindings = ReadVarintString(reader);
            ClientSubKeyBindings = ReadVarintString(reader);
        }
        catch (ProtocolException) { }
    }

    /// <summary>Gather result notice: combo start, the success broadcast and the updated daily count (2.0.13 only).</summary>
    List<byte[]> GatherResultNotices(int sourceKey, long now)
    {
        if (!IsV213) return [];
        var count = options.World?.GatherLimiter.Used(now) ?? 0;
        var broadcast = ModernPackets.BroadcastGatherSourceResult(playerKey, ModernPackets.GatherResultSuccess, sourceKey);
        // The success animation and sound play on the player actor, and nearby players see it too.
        worldPlayer?.Map?.Broadcast(worldPlayer, [broadcast]);
        return
        [
            ModernPackets.GatherComboStart(1, now + 30_000),
            broadcast,
            ModernPackets.GatherLimitStatus(count, now),
        ];
    }

    /// <summary>
    /// Adds looted Kina to the session's acquired total and, on 2.0.13, emits 0xFFA6 with the new total
    /// (OBSERVED in ingame-1.a2p when a monster kill pays Kina).
    /// </summary>
    List<byte[]> KinaLootStatus(long amount, long now)
    {
        if (!IsV213 || amount <= 0) return [];
        acquiredKina += amount;
        return [ModernPackets.KinaLimitStatus(acquiredKina, now)];
    }
}
