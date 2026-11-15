using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Skill bar presets (PresetSkillUpdate_NT 0xe293) and the inspect window (ViewChar 0x364f/0x3650).</summary>
public sealed class SkillPresetInspectTests
{
    const long Now = 1_800_000_000_000;
    static ushort Op(byte[] p) => BitConverter.ToUInt16(p, 0);

    /// <summary>The official PresetSkillUpdate_NT (a Gladiator's bar copied into preset 2).</summary>
    const string OfficialSkillUpdate = "93e2020c030200e026a80004e026a800000000000000000000000000030201d0ffa70004d0ffa700000000000000000000000000030202f0beaa0004f0beaa00d070aa0000000000000000000302039045ac00049045ac00000000000000000000000000030204109ca80004109ca800000000000000000000000000030205605fa90004605fa9000000000000000000000000000302060057ad00040057ad00a06cac000000000000000000030207801eac0004801eac0000e6aa0000000000000000000202140400000000000000000000000000000000020215040000000000000000000000000000000002021604000000000000000000000000000000000202170400000000000000000000000000000000";
    /// <summary>The official ViewChar_RS: bytes 0-69 and everything after the 28 worn items (the character and guild names replaced).</summary>
    const string OfficialHead = "5036000007065465737465720f00000002012d000000000000000f0e00000c090c093884bd40010400000000000c090447756c64020002000000000000000000000000000000";
    const string OfficialTail = "de020c6464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646478b467646464648cb965720101020101026464647373666611130c23231a220d0d271a1a64ffffffffffff0000591c1c591c1c67020202020202292967000000643233b58c86c868300f06c80069cd7e6651003c662b01023c3c006e8484846e6f006900000046000065000000830000006f8484846e6f00006e7e7e7e6f6f00691f050064003c653f0000830065653d0d0fc800c8056e8383836e6f0065200c086465000000796464650d0e0e6a6434376464646465703b349e7d2626658c6511162bc83c64807053c8a1646565656565656a69100b078200005e64511f6520130d652100000000000000000000000000000000000000000006011f0000004401200000003e01210000003b01220000004a01230000004d0124000000380301e51a5f0102b32650010319b75f01050209000401050205030404050505060507050804030900050105020403040404050506050704080404090005010402050305040405040604070508050509000401050205030504040504060407050805060900050105020403050405050406050705080504060e10141fff0300000000000501000000ff00000002000000090000000300000006000000040000000e000000050000000c000000";

    [Fact]
    public void PresetSkillUpdate_matches_the_official_bytes()
    {
        GameSession.SkillPresetSlot Slot(int slot, int skill, int context = 0) => new(slot, skill, [skill, context, 0, 0]);
        var gladiator = new List<GameSession.SkillPresetSlot>
        {
            Slot(0, 11020000), Slot(1, 11010000), Slot(2, 11190000, 11170000), Slot(3, 11290000), Slot(4, 11050000), Slot(5, 11100000),
            Slot(6, 11360000, 11300000), Slot(7, 11280000, 11200000),
        };
        gladiator.AddRange(new[] { 20, 21, 22, 23 }.Select(s => new GameSession.SkillPresetSlot(s, 0, [0, 0, 0, 0])));
        Assert.Equal(Convert.FromHexString(OfficialSkillUpdate), GameSession.PresetSkillUpdate(2, gladiator));
    }

    static byte[] PresetSet(byte type, byte no) => new PacketWriter().U16(0xe28f).U8(type).U8(no).U8(0).ToArray();

    [Fact]
    public async Task A_new_skill_preset_copies_the_bar_and_each_preset_keeps_its_own()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var toTwo = await session.HandleAsync(PresetSet(2, 2), Now + 1000);
        Assert.Equal(0xe293, Op(toTwo[0]));
        Assert.Equal(0xe290, Op(toTwo[1]));
        Assert.Contains(toTwo, p => Op(p) == 0x5100);
        Assert.Contains(toTwo, p => Op(p) == 0x5105);
        Assert.Equal(2, session.SkillPresetNoForChecks);
        var kept = session.SkillPresetsForChecks[1];
        Assert.Contains(kept, s => s.Skill != 0);
        Assert.Equal(GameSession.PresetSkillUpdate(2, kept), toTwo[0]);   // preset 2 started as a copy of preset 1
        Assert.True(store.Settings.ContainsKey("skill_presets"));

        // Empty slot 0 in preset 2 (the client's QuickSlotReset), then back to 1: no 0xe293, slot 0 has its skill again.
        var slot0 = kept.First(s => s.Slot == 0).Skill;
        Assert.NotEqual(0, slot0);
        await session.HandleAsync(new PacketWriter().U16(0x5603).U8(0).ToArray(), Now + 2000);
        var back = await session.HandleAsync(PresetSet(2, 1), Now + 3000);
        Assert.DoesNotContain(back, p => Op(p) == 0xe293);
        Assert.Equal(1, session.SkillPresetNoForChecks);
        Assert.Equal(0, session.SkillPresetsForChecks[2].First(s => s.Slot == 0).Skill);   // preset 2 keeps its emptied slot
        Assert.False(session.QuickSlots.TryGetValue(0, out var bar0) && bar0.SkillId != slot0);   // the bar is preset 1 again
    }

    static IReadOnlyList<(byte Slot, byte Grade)> Slots(params byte[] grades) => grades.Select((g, i) => ((byte)i, g)).ToList();

    [Fact]
    public void ViewChar_rebuilds_the_official_reply_around_its_items()
    {
        var official = new ViewCharInfo
        {
            Name = "Tester", PcId = 15, Race = 2, Gender = 1, Level = 45, EquipItemLevel = 3599, BornServerId = 2316, CurrentServerId = 2316, Online = false,
            TitleId = 21020036, GuildId = 0x090c000000000004, GuildName = "Guld", GuildEmblem = 2, GuildGrade = 2,
            CustomizingData = Convert.FromHexString(OfficialTail)[2..352],
            Boards = [(31, 68), (32, 62), (33, 59), (34, 74), (35, 77), (36, 56)], Titles = [(1, 23010021), (2, 22030003), (3, 23050009)],
            CreatureBoards = [(2, Slots(4, 5, 5, 4, 5, 5, 5, 5, 4)), (3, Slots(5, 5, 4, 4, 4, 5, 5, 4, 4)), (4, Slots(5, 4, 5, 5, 4, 4, 4, 5, 5)),
                (5, Slots(4, 5, 5, 5, 4, 4, 4, 5, 5)), (6, Slots(5, 5, 4, 5, 5, 4, 5, 5, 5))],
            Unlocks = [6, 14, 16, 20], CombatPower = 261919, RoleVotes = [(1, 255), (2, 9), (3, 6), (4, 14), (5, 12)],
        };
        var ours = InspectPackets.ViewChar(0, official);
        var head = Convert.FromHexString(OfficialHead);
        // Byte 34 holds the packed bools (online and the official items' flags); 70 is the item count (28 officially, none here).
        Assert.Equal(head.AsSpan(0, 34).ToArray(), ours.AsSpan(0, 34).ToArray());
        Assert.Equal(head.AsSpan(35, 35).ToArray(), ours.AsSpan(35, 35).ToArray());
        Assert.Equal(0, ours[70]);
        Assert.Equal(Convert.FromHexString(OfficialTail), ours.AsSpan(71).ToArray());
        Assert.Equal((2316, 77L), InspectPackets.ParseViewChar(new PacketWriter().U16(0x364f).U16(2316).I64(77).ToArray()));
        Assert.Equal((2316, 77L), InspectPackets.ParseViewChar(new PacketWriter().U16(0x364f).U16(2316).Varint(77).ToArray()));
        Assert.Null(InspectPackets.ParseViewChar(new byte[] { 0x4f, 0x36 }));
    }

    [Fact]
    public async Task ViewChar_answers_an_online_character_and_refuses_an_unknown_one()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now);
        var self = Assert.Single(await session.HandleAsync(new PacketWriter().U16(0x364f).U16(1).I64(session.WireDbIdForTests).ToArray(), Now + 10));
        Assert.Equal(0x3650, Op(self));
        Assert.Equal(0, BitConverter.ToUInt16(self, 2));
        Assert.Equal(0x07, self[4]);
        var nobody = Assert.Single(await session.HandleAsync(new PacketWriter().U16(0x364f).U16(1).I64(987654321).ToArray(), Now + 20));
        Assert.Equal(InspectPackets.NotFoundCharacter, BitConverter.ToUInt16(nobody, 2));
    }
}
