using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    /// <summary>Spirit owner buff (Fire/Water ActiveSkillList); capture 000220.</summary>
    public const int SpiritOwnerBuffSkillId = 16_107_000;
    public const int SpiritOwnerBuffDelayMs = 85;
    public const int DimensionalControlSkillId = 16_330_000;
    /// <summary>SkillCond Self HasAbnormalGroupId gate for Dimensional Control.</summary>
    public const int DimensionalControlEnablerAbnormal = 163_300_000;
    public const int DimensionalControlEnablerMs = 3000;
    public const int DimensionalControlTargetAbnormal = 163_300_001;
    public const byte AbnormalExpireSkillConsume = 11;

    /// <summary>Water spirit owner buff: 0x3802/0x3804 from summon to player, then 0x382a enabler abnormal.</summary>
    List<byte[]> ApplySpiritOwnerBuff(Target spirit, long now)
    {
        if (!elementalModes.TryGetValue(spirit.Key, out var group)) return [];
        var replies = SummonAi.SpiritOwnerBuffOnPlayer(spirit, playerKey, SpiritOwnerBuffSkillId).ToList();
        if (group == ElementalSummons.GroupType("Water"))
            AddBuffFromCaster(DimensionalControlEnablerAbnormal, DimensionalControlEnablerMs, SpiritOwnerBuffSkillId, spirit.Key, now, replies);
        return replies;
    }

    /// <summary>On Dimensional Control accept: consume the enabler (0x382c reason 0x0b + skill id).</summary>
    internal List<byte[]> DimensionalControlCastReplies(int skill, long now)
    {
        if (skill != DimensionalControlSkillId) return [];
        return ConsumeDimensionalControlEnabler(now);
    }

    internal void GrantDimensionalControlEnablerForTest(long now)
    {
        var replies = new List<byte[]>();
        AddBuff(DimensionalControlEnablerAbnormal, DimensionalControlEnablerMs, 0, now, replies, path: SelfAbnormalPath.Scripted);
    }

    List<byte[]> ConsumeDimensionalControlEnabler(long now)
    {
        var gone = buffs.Where(b => b.AbnormalId == DimensionalControlEnablerAbnormal || b.Group == DimensionalControlEnablerAbnormal).ToList();
        if (gone.Count == 0) return [];
        foreach (var b in gone) buffs.Remove(b);
        MarkBuffSettingsDirty();
        var remove = PlayerPackets.AbnormalRemoveSkillConsume(playerKey, gone[0].Uid, AbnormalExpireSkillConsume, playerKey, DimensionalControlSkillId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [remove]);
        return [remove, .. SkillCondContextExpireReplies(gone)];
    }
}
