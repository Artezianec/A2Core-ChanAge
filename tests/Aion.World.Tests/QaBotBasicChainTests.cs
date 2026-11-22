using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotBasicChainTests
{
    static readonly GameData Data = GameData.Embedded;
    const long T0 = 1_750_000_200_000;

    static readonly (string Class, int Root, int Step2, int Step3)[] ExpectedChains =
    [
        ("Gladiator", 11020000, 11030000, 11040000),
        ("Templar", 12010000, 12020000, 12030000),
        ("Assassin", 13010000, 13030000, 13040000),
        ("Ranger", 14020000, 14030000, 14040000),
        ("Sorcerer", 15210000, 15030000, 15250000),
        ("Elementalist", 16010000, 16020000, 16030000),
        ("Cleric", 17010000, 17020000, 17030000),
        ("Chanter", 18010000, 18020000, 18030000),
    ];

    static int CreationOf(string cls) => Enumerable.Range(1, 64).First(c => Data.Profile(c, 1) is { Race: 1, Class: var cl }
        && cl.Equals(cls, StringComparison.OrdinalIgnoreCase));

    static async Task<GameSession> EnterClassAsync(string cls, int level = 10)
    {
        var creation = CreationOf(cls);
        var profile = Data.Profile(creation, 1)!;
        var store = new FakeCharacterStore { Progress = (level, 0) };
        if (Data.StarterWeapon(profile) is { } weaponId)
            store.Items.Add(FakeCharacterStore.EquippedItem(weaponId, 900, 1));
        var shared = new SharedWorld(Data);
        var session = new GameSession(Data,
            new GameSessionOptions { World = shared, CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            new Random(41))
        { Store = store, Bot = true, QaBot = true };
        await session.HandleAsync(BotRequests.GameLogin($"qa-chain-{cls}"), T0);
        await session.HandleAsync(BotRequests.Create($"qa-chain-{cls}", creation), T0);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(BotRequests.Select((uint)wireId << 16), T0);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0);
        await GameSessionTestHelpers.LoadInventoryAsync(session, T0);
        Assert.Equal(cls, session.ClassName, ignoreCase: true);
        return session;
    }

    [Theory]
    [MemberData(nameof(ChainCases))]
    public void Basic_chain_steps_match_class_skills_json(string cls, int root, int step2, int step3)
    {
        _ = cls;
        var rootDef = Data.Skill(root);
        Assert.NotNull(rootDef);
        Assert.True(SkillCooldown.IsBasicAutoAttack(rootDef!));
        Assert.Equal(0, rootDef.ChainPrev);
        Assert.Contains(step2, Data.ChainFollowUpIds(root));
        Assert.Contains(step3, Data.BasicAttackChainSkillIds(root, 1));
    }

    [Theory]
    [MemberData(nameof(ChainCases))]
    public async Task BasicAttackSkill_picks_chain_root_not_mid_combo_step(string cls, int root, int step2, int step3)
    {
        _ = step3;
        var session = await EnterClassAsync(cls);
        Assert.Equal(root, QaBotCombat.BasicChainRoot(Data, session));
        Assert.Equal(root, QaBotCombat.BasicAttackSkill(Data, session, T0));
        Assert.NotEqual(step2, QaBotCombat.BasicAttackSkill(Data, session, T0));
    }

    [Fact]
    public async Task BasicAttackSkill_advances_when_chain_window_open()
    {
        var session = await EnterClassAsync("Templar");
        const int root = 12010000, step2 = 12020000;
        var dummy = session.Targets[Combat.TrainingDummyKey];
        var at = session.WorldPosition!.Value;
        var cast = new PacketWriter().U16(0x3800).U16(16).I32(root).U8(2).Varint(dummy.Key).F32(0).Vec3(at).U8(9).ToArray();
        var replies = await session.HandleAsync(cast, T0 + 50);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.True(session.ActiveChainSkills.ContainsKey(step2));
        var now = T0 + 200;
        Assert.Equal(step2, QaBotCombat.BasicAttackSkill(Data, session, now));
    }

    [Fact]
    public async Task BasicAttackSkill_picks_third_chain_step_when_only_step3_window_open()
    {
        var session = await EnterClassAsync("Gladiator");
        const int root = 11020000, step2 = 11030000, step3 = 11040000;
        var dummy = session.Targets[Combat.TrainingDummyKey];
        var at = session.WorldPosition!.Value;
        var castRoot = new PacketWriter().U16(0x3800).U16(16).I32(root).U8(2).Varint(dummy.Key).F32(0).Vec3(at).U8(9).ToArray();
        await session.HandleAsync(castRoot, T0 + 50);
        var cast2 = new PacketWriter().U16(0x3800).U16(16).I32(step2).U8(2).Varint(dummy.Key).F32(0).Vec3(at).U8(9).ToArray();
        var replies2 = await session.HandleAsync(cast2, T0 + 900);
        Assert.Contains(replies2, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.True(session.ActiveChainSkills.ContainsKey(step3), "step-3 window should be open after step-2");
        var pick = QaBotCombat.BasicAttackSkill(Data, session, T0 + 950);
        Assert.Equal(step3, pick);
    }

    public static IEnumerable<object[]> ChainCases() =>
        ExpectedChains.Select(c => new object[] { c.Class, c.Root, c.Step2, c.Step3 });
}