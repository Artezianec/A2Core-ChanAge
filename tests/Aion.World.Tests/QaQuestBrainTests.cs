using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>QA quest brain: stuck detection fingerprint and step driver slices (docs/QA_BOTS.md).</summary>
public sealed class QaQuestBrainTests
{
    static readonly GameData Data = GameData.Embedded;
    const long Now = 1_700_100_000_000;

    [Fact]
    public void ProgressFingerprint_changes_when_goals_change()
    {
        var world = new SharedWorld(Data);
        var session = new GameSession(Data, new GameSessionOptions { World = world }, new Random(1)) { Bot = true, QaBot = true };
        session.DevelopmentQuestLog();
        var code = "HQ1101010";
        session.Quests!.Set(QuestLog.New(code, session.Quests.Book.Find(code)!));
        var a = QuestStepDriver.ProgressFingerprint(session, code);
        var record = session.Quests.Get(code)! with { Goals = [1, 0, 0, 0, 0, 0] };
        session.Quests.Set(record);
        var b = QuestStepDriver.ProgressFingerprint(session, code);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void QaBotOptions_visible_defaults_true() => Assert.True(new QaBotOptions().Visible);

    [Fact]
    public void QaBotOptions_enabled_defaults_true() => Assert.True(new QaBotOptions().Enabled);

    [Fact]
    public void QaBotOptions_auto_run_defaults_on() => Assert.True(new QaBotOptions().AutoRun.Enabled);

    [Fact]
    public void QaBotOptions_effective_pool_clamps_auto_count()
    {
        var o = new QaBotOptions { MaxBots = 3, HardMaxBots = 8, AutoRun = new QaAutoRunOptions { Count = 5 } };
        Assert.Equal(3, o.EffectivePoolCount());
    }

    [Fact]
    public void QaBotOptions_profile_for_slot_cycles_classes_and_races()
    {
        var o = new QaBotOptions { AutoRun = new QaAutoRunOptions { Classes = ["Gladiator", "Cleric"] } };
        Assert.Equal(("Gladiator", 1), o.ProfileForSlot(0));
        Assert.Equal(("Cleric", 2), o.ProfileForSlot(1));
        Assert.Equal(("Gladiator", 1), o.ProfileForSlot(2));
    }

    [Fact]
    public void StuckReason_codes_are_stable()
    {
        Assert.Equal("step_timeout", QaStuckReason.StepTimeout);
        Assert.Equal("npc_missing", QaStuckReason.NpcMissing);
        Assert.Equal("unsupported_objective", QaStuckReason.UnsupportedObjective);
    }

    [Fact]
    public void Driver_stuck_slice_carries_reason()
    {
        var slice = QuestDriverSlice.Stuck("npc_not_spawned", "talk");
        Assert.Equal(QuestDriverKind.Stuck, slice.Kind);
        Assert.Equal("npc_not_spawned", slice.StuckReason);
    }


    [Fact]
    public void HeroChains_embedded_has_light_and_dark()
    {
        var chains = HeroChains.Embedded;
        Assert.True(chains.Light.Length > 10);
        Assert.True(chains.Dark.Length > 10);
        Assert.Equal("HQ1100010", chains.Light[0]);
    }
}