using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Skill specialisation parts (0x3900-0x3907; adapted from Addiboii/A2Core a1c5058).</summary>
public sealed class SpecializationTests
{
    const long Now = 1_760_003_000_000;

    static List<byte[]> Packets(IEnumerable<byte[]> replies, ushort op) => replies.Where(p => GameSessionTestHelpers.Op(p) == op).ToList();

    /// <summary>0x3900 rows: {skill i32, count varint, {slot u8, part i32}}.</summary>
    static List<(int Skill, List<(int Slot, int Part)> Slots)> Load(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var rows = new List<(int, List<(int, int)>)>();
        var count = r.Varint();
        for (var i = 0; i < count; i++)
        {
            var skill = (int)r.U32();
            var n = r.Varint();
            var slots = new List<(int, int)>();
            for (var j = 0; j < n; j++) slots.Add((r.U8(), (int)r.U32()));
            rows.Add((skill, slots));
        }
        Assert.True(r.End);
        return rows;
    }

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (store.States.Count == 0) await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        return (session, entry);
    }

    static byte[] Equip(int part, byte slot) => new PacketWriter().U16(GameSession.SpecializedEquipRq).I32(part).U8(slot).ToArray();
    static byte[] Unequip(int part) => new PacketWriter().U16(GameSession.SpecializedUnequipRq).I32(part).ToArray();

    [Fact]
    public async Task World_entry_lists_only_mastery_skills_with_three_empty_slots()
    {
        var (_, entry) = await EnterAsync(new FakeCharacterStore());
        var rows = Load(Assert.Single(Packets(entry, GameSession.SpecializedSkillLoadNt)));
        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.True(SpecializedSkillTables.Embedded.HasParts(r.Skill));
            Assert.Equal([(1, 0), (2, 0), (3, 0)], r.Slots);
        });
    }

    [Fact]
    public async Task Equip_needs_the_slot_level_then_adds_the_child_and_survives_a_relog()
    {
        var store = new FakeCharacterStore();
        var (first, entry) = await EnterAsync(store);
        var parent = Load(Packets(entry, GameSession.SpecializedSkillLoadNt)[0])[0].Skill;
        var part = parent + 30;   // parts +10..+50; +30 needs skill level 8 (slot 1 opens at 8)

        var refused = await first.HandleAsync(Equip(part, 1), Now + 1_000);
        var reply = Assert.Single(Packets(refused, GameSession.SpecializedEquipRs));
        Assert.NotEqual(0, BitConverter.ToUInt16(reply, 2));
        Assert.Empty(store.SkillPartRows);

        // The skill at level 8 (bought), on the next login.
        store.Skills.Add(new SkillState(parent, 8, null, 8));
        var (session, _) = await EnterAsync(store);
        var equipped = await session.HandleAsync(Equip(part, 1), Now + 2_000);
        var ops = equipped.Select(GameSessionTestHelpers.Op).ToList();
        var add = Assert.Single(Packets(equipped, GameSession.SkillAddNt));
        Assert.Equal(parent + 30, BitConverter.ToInt32(add, 3));   // the child: parent + the parts' digits
        Assert.True(ops.IndexOf(GameSession.SkillAddNt) < ops.IndexOf(GameSession.SpecializedEquipRs));
        Assert.Equal(0, BitConverter.ToUInt16(Assert.Single(Packets(equipped, GameSession.SpecializedEquipRs)), 2));
        Assert.Equal(GameSession.SpecializedUpdateNt, ops[^1]);
        Assert.Equal([new SkillPartRecord(parent, 1, part)], store.SkillPartRows);

        var (_, again) = await EnterAsync(store);
        var row = Load(Packets(again, GameSession.SpecializedSkillLoadNt)[0]).Single(r => r.Skill == parent);
        Assert.Equal((1, part), row.Slots[0]);

        var off = await session.HandleAsync(Unequip(part), Now + 3_000);
        Assert.Equal(parent + 30, BitConverter.ToInt32(Assert.Single(Packets(off, GameSession.SkillDeleteNt)), 2));
        Assert.Empty(store.SkillPartRows);
    }

    [Fact]
    public void A_child_casts_with_its_own_row_and_the_parent_group_cooldown()
    {
        var child = GameData.Embedded.Skill(11100010);
        Assert.NotNull(child);
        Assert.Equal(45000, child!.CooldownMs);
        Assert.Equal(250, child.Cost.Mp);
        Assert.Equal(11100000, GameData.Embedded.CooldownGroup(11100010));
        Assert.Equal(GameData.Embedded.CooldownGroup(11100000), GameData.Embedded.CooldownGroup(11100010));
        Assert.Null(GameData.Embedded.Skill(11100009));
    }
}
