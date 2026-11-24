using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Item-granted skill points (UseEffect GetSkillPoint; tools/data/boxes.json 531900001 Wisdom Stone).
// The client tracks them separately from levelling totals (0x3633 item skill-point list; OBSERVED official captures).
// Remaining mastery points = Exp.dat total at level + bonus from items − spent (0x5105 cumulative).
public sealed partial class GameSession
{
    public const ushort UpdateItemSkillPointNt = 0x36bb;

    int skillBonusPoints;

    public int SkillBonusPoints => Math.Max(0, skillBonusPoints);

    void LoadSkillBonus(int bonus) => skillBonusPoints = Math.Max(0, bonus);

    /// <summary>0x36BB after a Wisdom Stone use: count 1, then the granted points for this use (shape mirrors UpdateItemDaevanionPoint_NT).</summary>
    byte[] UpdateItemSkillPointWire(int grant) =>
        PacketWriter.Opcode(UpdateItemSkillPointNt).U8(1).U8(SkillPointTypePoint).I32(grant).ToArray();
}