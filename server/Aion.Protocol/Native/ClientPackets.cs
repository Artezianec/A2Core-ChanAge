using System.Text;

namespace Aion.Protocol.Native;

/// <summary>Client-to-server bodies (c2s). Layouts mirror capture tooling and tools/load_test.py.</summary>
public static class ClientPackets
{
    public static byte[] LoginIdentity(string authnToken) => LengthPrefixedStrings(0x3905, "", authnToken, "", "", "", "");
    public static byte[] LoginCharacterList() => Opcode(0x390a);
    public static byte[] LoginWorldDirectory() => Opcode(0x3908);
    public static byte[] LoginPing(ulong counter) => PacketWriter.Opcode(0x3902).U64(counter).ToArray();
    public static byte[] LoginWorldSelect(int worldId) => PacketWriter.Opcode(0x390c).U16(worldId).ToArray();
    public static byte[] LoginRedirect(int worldId) => PacketWriter.Opcode(0x390e).U16(worldId).ToArray();

    public static byte[] GameIdentity(string accountSession, string authnToken) =>
        [.. LengthPrefixedStrings(0x3613, accountSession, "", authnToken, ""), .. Zeros(9)];

    public static byte[] GameNameCheck(string name)
    {
        var body = LengthPrefixedStrings(0x3619, name);
        return [.. body, 0x01];
    }

    public static byte[] GameCreate(string name, int creationId = 1, byte[]? customizing = null, int voice = 2, int slot = 1)
    {
        customizing ??= [0x0c, 0x64];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 255) throw new ArgumentException("Name too long", nameof(name));
        return PacketWriter.Opcode(0x361b).U8(nameBytes.Length).Raw(nameBytes).U8(1).I32(creationId)
            .Bytes(customizing).U8(voice).U8(slot).U8(0).Zeros(2).ToArray();
    }

    /// <summary>Minimal select body observed from the development client (varint key + 16 zero bytes).</summary>
    public static byte[] GameSelectCharacter(uint key) => PacketWriter.Opcode(0x361f).Varint(key).Zeros(16).ToArray();

    public static byte[] GameLoadComplete(int ticket = 1) => PacketWriter.Opcode(0x3622).I32(ticket).ToArray();

    public static byte[] GamePing(ulong counter) => PacketWriter.Opcode(0x3602).U64(counter).ToArray();

    /// <summary>0x3700/0x3701 position report: mask u8, xyz f32, yaw u16, heading f32.</summary>
    public static byte[] PositionReport(ushort opcode, Vector3d position, float headingDegrees)
    {
        var yaw = (int)(headingDegrees / 360f * 65536) & 0xffff;
        return PacketWriter.Opcode(opcode).U8(0).F32(position.X).F32(position.Y).F32(position.Z).U16(yaw).F32(headingDegrees).ToArray();
    }

    /// <summary>0x3800 skill cast (Keen Strike layout from load_test).</summary>
    public static byte[] SkillCast(ushort opcode, int skillId, uint targetKey, Vector3d? targetPos = null)
    {
        var pos = targetPos ?? Vector3d.Zero;
        return PacketWriter.Opcode(opcode).U16(16).U32((uint)skillId).U8(2).Varint(targetKey)
            .F32(pos.X).F32(pos.Y).F32(pos.Z).F32(0).Zeros(1).ToArray();
    }

    /// <summary>0x8d0a QuestAccept_RQ (usmap guess; see GameSession quest handlers).</summary>
    public static byte[] QuestAccept(int questId) => PacketWriter.Opcode(0x8d0a).I32(questId).ToArray();

    /// <summary>0x8d0c QuestGiveUp_RQ.</summary>
    public static byte[] QuestGiveUp(int questUid) => PacketWriter.Opcode(0x8d0c).I32(questUid).ToArray();

    /// <summary>0x8d0e QuestTakeReward_RQ.</summary>
    public static byte[] QuestTakeReward(int questUid) => PacketWriter.Opcode(0x8d0e).I32(questUid).ToArray();

    /// <summary>0x8c02 NPC interaction start (ask dialog).</summary>
    public static byte[] NpcInteract(int npcKey) => PacketWriter.Opcode(0x8c02).Varint(npcKey).ToArray();

    /// <summary>0xe28f Builder chat / Say (layout from live traces; channel byte + UTF-8 text).</summary>
    public static byte[] ChatSay(byte channel, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 512) throw new ArgumentException("Chat text too long", nameof(text));
        return PacketWriter.Opcode(0xe28f).U8(channel).Varint(bytes.Length).Raw(bytes).ToArray();
    }

    public static string HeadlessAuthToken(string sharedSecret, int accountIndex) =>
        $"headless:{sharedSecret}:{accountIndex}";

    public const string DevelopmentAuthToken = "local-dev";

    static byte[] Opcode(ushort opcode) => PacketWriter.Opcode(opcode).ToArray();

    static byte[] LengthPrefixedStrings(ushort opcode, params string[] values)
    {
        var w = PacketWriter.Opcode(opcode);
        foreach (var value in values)
        {
            var raw = Encoding.UTF8.GetBytes(value);
            if (raw.Length > 255) throw new ArgumentException("String exceeds u8 length", nameof(values));
            w.U8(raw.Length).Raw(raw);
        }
        return w.ToArray();
    }

    static byte[] Zeros(int count) => new byte[count];
}