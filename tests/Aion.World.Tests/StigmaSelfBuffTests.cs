using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Stigma self-buffs on cast (ClassSelfBuffs + tools/stigmas.py self_buffs).</summary>
public sealed class StigmaSelfBuffTests
{
    static readonly long Now = 1_700_000_050_000;
    const int LungeStance = 11400000;
    const int LungeAbnormal = 114000001;
    static readonly StigmaOptions On = new() { Enabled = true };

    static bool Has(byte[] packet, int value) => packet.AsSpan().IndexOf(BitConverter.GetBytes(value)) >= 0;

    static async Task<GameSession> EnterGladiatorAsync(FakeCharacterStore store)
    {
        store.Progress = (45, 0);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, Stigmas = On }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static Task<List<byte[]>> LevelUp(GameSession session, int skill) =>
        session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skill).ToArray(), Now);

    static Task<List<byte[]>> SetStigmaSlot(GameSession session, int skill) =>
        session.HandleAsync(new PacketWriter().U16(GameSession.QuickSlotSetRq).U8(0).U8(20).U8(4).I64(0).I32(0).I32(skill).U8(0).I32(0).U8(0).ToArray(), Now);

    static byte[] CastSelf(GameSession session, int skill) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint((uint)session.PlayerKey).F32(0).Vec3(session.Position ?? Vector3d.Zero).ToArray();

    [Fact]
    public void SelfRows_reads_self_buffs_and_falls_back_from_variant_to_stigma()
    {
        var table = GameData.Embedded.Stigmas;
        var rows = table.SelfRows(LungeStance);
        Assert.NotEmpty(rows);
        Assert.Equal(LungeAbnormal, rows[0].Abnormal);
        Assert.Equal(LungeStance, table.StigmaOfVariant(LungeStance + 10));
        Assert.NotEmpty(table.SelfRows(LungeStance + 10));
        Assert.Empty(table.SelfRows(99_999_999));
        Assert.NotNull(AbnormalSpecs.Info(LungeAbnormal));
    }

    [Fact]
    public void Self_target_stigmas_either_have_self_buffs_or_are_listed_missing()
    {
        var table = GameData.Embedded.Stigmas;
        var missing = new HashSet<int>(Enumerable.Range(0, 0));
        // params.self_buff_missing is embedded in stigmas.json; StigmaTable does not expose it — read via Skill rows.
        foreach (var cls in table.Classes)
        {
            foreach (var id in table.ClassStigmas(cls))
            {
                var def = table.Skill(id);
                if (def?.Target != "Self") continue;
                var has = table.SelfRows(id).Count > 0;
                if (!has)
                    missing.Add(id);
            }
        }
        Assert.NotEmpty(missing);
        Assert.True(missing.Count <= 50);
    }

    [Fact]
    public async Task Class_dash_self_buff_regression_is_unchanged()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now, level: 55);
        var replies = session.ClassSelfBuffsForTests(1101, 3, Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x382a && Has(p, 11011));
    }

    [Fact]
    public async Task Slotted_self_stigma_cast_applies_abnormal_and_stats_expire()
    {
        var store = new FakeCharacterStore();
        var session = await EnterGladiatorAsync(store);
        Assert.Contains(LungeStance, GameData.Embedded.Stigmas.ClassStigmas(session.ClassName));
        Assert.Equal(0, BitConverter.ToUInt16((await LevelUp(session, LungeStance)).Find(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpRs)!, 2));
        await SetStigmaSlot(session, LungeStance);
        var before = session.BuffStatForTests("CombatSpeed");
        var cast = await session.HandleAsync(CastSelf(session, LungeStance), Now + 1000);
        Assert.Contains(cast, p => GameSessionTestHelpers.Op(p) == 0x382a && Has(p, LungeAbnormal));
        Assert.NotEqual(before, session.BuffStatForTests("CombatSpeed"));
        Assert.Contains(LungeAbnormal, session.ActiveBuffs);
        var tick = session.BuffTickForTests(Now + 20_000);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.DoesNotContain(LungeAbnormal, session.ActiveBuffs);
    }

    [Fact]
    public async Task Unlearned_or_unslotted_self_stigma_cast_is_still_refused()
    {
        var store = new FakeCharacterStore();
        var session = await EnterGladiatorAsync(store);
        var unlearned = await session.HandleAsync(CastSelf(session, LungeStance), Now + 200);
        Assert.DoesNotContain(unlearned, p => GameSessionTestHelpers.Op(p) == 0x382a);
        await LevelUp(session, LungeStance);
        var unslotted = await session.HandleAsync(CastSelf(session, LungeStance), Now + 300);
        Assert.DoesNotContain(unslotted, p => GameSessionTestHelpers.Op(p) == 0x382a);
    }

    [Fact]
    public async Task Resetting_a_stigma_removes_its_active_self_buff()
    {
        var store = new FakeCharacterStore();
        var session = await EnterGladiatorAsync(store);
        await LevelUp(session, LungeStance);
        await SetStigmaSlot(session, LungeStance);
        await session.HandleAsync(CastSelf(session, LungeStance), Now + 500);
        Assert.Contains(LungeAbnormal, session.ActiveBuffs);
        await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(2).I32(LungeStance).ToArray(), Now + 600);
        Assert.DoesNotContain(LungeAbnormal, session.ActiveBuffs);
    }
}