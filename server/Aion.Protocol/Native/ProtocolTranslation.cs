namespace Aion.Protocol.Native;

/// <summary>
/// Opcode translation between the pinned 2.0.9 server tables and the 2.0.12 dispatch table.
/// The server keeps its 2.0.9 handlers; a modern connection has only the header rewritten at
/// the frame edges. Layout changes at unchanged opcodes are handled by their own writers.
/// </summary>
public static class ProtocolTranslation
{
    static readonly Dictionary<ushort, ushort> To209 = [];
    static readonly Dictionary<ushort, ushort> To212 = [];

    static void Pair(ushort v209, ushort v212)
    {
        To209[v212] = v209;
        To212[v209] = v212;
    }

    static void Range(ushort first209, ushort last209, int delta)
    {
        for (var value = (int)first209; value <= last209; value++)
            Pair((ushort)value, (ushort)(value + delta));
    }

    static ProtocolTranslation()
    {
        // Party loot / dice: 0x3813-0x3828 and 0x3830 -> 0x5718-0x572D and 0x5735.
        // 0x3829-0x382F stay untouched: 0x382A/0x382C are the abnormal add/remove packets.
        for (var value = 0x3813; value <= 0x3828; value++)
            Pair((ushort)value, (ushort)(value + 0x1F05));
        Pair(0x3830, 0x5735);
        // Trade: 0x3B69-0x3B77 -> 0xE264-0xE272.
        Range(0x3B69, 0x3B77, 0xA6FB);
        // Party dungeon rooms: 0x3AC1-0x3AF5 -> 0x9700-0x9734.
        Range(0x3AC1, 0x3AF5, 0x5C3F);
        // Arena / matching: 0xE2A8-0xE2C5 -> 0xE30F-0xE32C.
        Range(0xE2A8, 0xE2C5, 0x67);
        // Duel: 0x933C-0x934A -> 0x9400-0x940E.
        Range(0x933C, 0x934A, 0xC4);
        // Airspace battle: 0x372C-0x373A -> 0x4300-0x430E.
        Range(0x372C, 0x373A, 0xBD4);
        // Godstone: 0x3776-0x377B -> 0x5634-0x5639.
        Range(0x3776, 0x377B, 0x1EBE);
        // Reward limits / abyss points: 0x3C04-0x3C06 -> 0xFFA9-0xFFAB.
        Range(0x3C04, 0x3C06, 0xC3A5);

        // Field boss tab.
        Pair(0x3A0C, 0x9100); Pair(0x3A0D, 0x9101);
        // Party dungeon entry / bookmarks / challenge / clear / cooltimes.
        Pair(0x3A60, 0x9322); Pair(0x3A61, 0x9323); Pair(0x3A62, 0x9324);
        Pair(0x3A66, 0x9330); Pair(0x3A67, 0x9331); Pair(0x3A69, 0x9333);
        // Monolith and account-achievement sync.
        Pair(0x3BA9, 0xE30B); Pair(0x3BAA, 0xE30C); Pair(0x3BAB, 0xE30D); Pair(0x3BAC, 0xE30E);
        Pair(0x3BDF, 0xE341); Pair(0x3C07, 0xFFAC); Pair(0x3C08, 0xFFAD);
        // Force / legion alliance.
        Pair(0x3A8A, 0x9600); Pair(0x3A8B, 0x9601); Pair(0x3ABD, 0x9633); Pair(0x3ABE, 0x9634);
        Pair(0x3AAE, 0x9624); Pair(0x3AAF, 0x9625);
        // Achievement close notices.
        Pair(0xE2C8, 0xE32F); Pair(0xE2C9, 0xE330); Pair(0xE2CA, 0xE331); Pair(0xE2CB, 0xE332);
        // Daevanion open-all.
        Pair(0xE2EE, 0xE500); Pair(0xE2EF, 0xE501);
        // Single moves.
        Pair(0x3BD1, 0xE333);   // RefreshCurrency
        Pair(0x36EE, 0x3845); Pair(0x36EF, 0x3846);   // skill customizing
        Pair(0x930D, 0xE21D); Pair(0x930E, 0xE21E);   // exit instance layer
        Pair(0x9327, 0x931F);   // party leave
        // Abyss rift.
        Pair(0x385B, 0x6302); Pair(0x385C, 0x6303); Pair(0x385E, 0x6305); Pair(0x385F, 0x6306);
    }

    static byte[] Rewrite(byte[] body, Dictionary<ushort, ushort> map)
    {
        if (body.Length < 2) return body;
        var opcode = (ushort)(body[0] | body[1] << 8);
        if (!map.TryGetValue(opcode, out var mapped)) return body;
        var copy = (byte[])body.Clone();
        copy[0] = (byte)mapped;
        copy[1] = (byte)(mapped >> 8);
        return copy;
    }

    /// <summary>A 2.0.12 client body translated to the 2.0.9 handler's opcode.</summary>
    public static byte[] ToServer(byte[] body) => Rewrite(body, To209);

    /// <summary>A 2.0.9 handler reply translated to the 2.0.12 client's opcode.</summary>
    public static byte[] ToClient(byte[] body) => Rewrite(body, To212);
}
