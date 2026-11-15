using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Daily dungeons (WaveDungeonInfo 0x6103, DungeonPoint 0x6104, WaveDungeonCompleteResult 0x6102) and the buff choice 0x6105-0x6107.</summary>
public sealed class DailyDungeonTests
{
    const long Now = 1_800_000_000_000;
    const int CoinEnchant = 930100023, Gold = 930100001;
    static ushort Op(byte[] b) => BitConverter.ToUInt16(b, 0);

    [Fact]
    public void The_tables_carry_points_rewards_and_buffs()
    {
        var dailies = DailyDungeons.Embedded;
        var wave = dailies.Of(900001)!;
        var coins = dailies.Of(900003)!;
        Assert.Equal(3, dailies.All.Count);
        Assert.Equal((900001, 90, 10, 30), (wave.Map, wave.LimitS, wave.ReadyS, wave.HoldS));
        Assert.Equal(3, wave.BuffSlots.Count);
        Assert.All(wave.BuffSlots.Values, v => Assert.Equal(12, v.Count));
        Assert.Equal(new DungeonBuffRow(1, 19003001, "Special"), wave.BuffSlots[1][0]);
        Assert.Empty(coins.BuffSlots);
        Assert.Equal(10, DailyDungeons.KillPoints(wave, 2701415));
        Assert.Equal(1000, DailyDungeons.KillPoints(wave, 2701427));
        Assert.Equal(0, DailyDungeons.KillPoints(wave, 2701426));
        Assert.Equal(450, DailyDungeons.UsePoints(coins, 1402033));
        Assert.Null(DailyDungeons.Reward(wave, 1, 90));
        Assert.Equal((CoinEnchant, 3000L), DailyDungeons.Reward(wave, 1, 100)!.Items[0]);
        Assert.Equal(5800L, DailyDungeons.Reward(wave, 2, 4170)!.Items[0].Count);
        Assert.Equal((Gold, 110000L), DailyDungeons.Reward(coins, 1, 500)!.Items[0]);
    }

    [Fact]
    public void The_offer_is_one_buff_per_slot_and_fixed_for_the_day()
    {
        var wave = DailyDungeons.Embedded.Of(900001)!;
        var day = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var offer = GameSession.DungeonBuffOffer(wave, 77, day);
        Assert.Equal([1, 2, 3], offer.Select(o => o.Slot));
        Assert.All(offer, o => Assert.Contains(o.Buff, wave.BuffSlots[o.Slot]));
        Assert.Equal(offer, GameSession.DungeonBuffOffer(wave, 77, day + 22 * 3600_000L));
        Assert.Contains(Enumerable.Range(1, 6), d => !GameSession.DungeonBuffOffer(wave, 77, day + d * 86_400_000L).SequenceEqual(offer));
    }

    [Fact]
    public void The_messages_match_the_official_and_usmap_forms()
    {
        Assert.Equal(Convert.FromHexString("03610207a1bb0d00015aca2007a2bb0d00013ca823"),
            DailyDungeons.WaveDungeonInfo([new DailyRecord(900002, 1, 60, 4520), new DailyRecord(900001, 1, 90, 4170)]));
        Assert.Equal(Convert.FromHexString("036100"), DailyDungeons.WaveDungeonInfo([]));
        Assert.Equal(Convert.FromHexString("026107a1bb0d000190bf05ca2001" + "37337037" + "a816000000000000"),
            DailyDungeons.WaveDungeonCompleteResult(900001, 1, 90_000, 4170, 0, [(CoinEnchant, 5800)]));
        Assert.Equal(Convert.FromHexString("0561a1bb0d0003" + "0100000003000000" + "0200000007000000" + "03000000ed030000"),
            DailyDungeons.DungeonBuffInfo(900001, [(2, 7), (1, 3), (3, 1005)]));
        Assert.Equal(Convert.FromHexString("07610000a1bb0d0002000000"), DailyDungeons.DungeonBuffSelectReply(0, 900001, 2));
    }

    static byte[] Pick(int dungeon, int slot) => new PacketWriter().U16(DailyDungeons.DungeonBuffSelectRq).I32(dungeon).I32(slot).ToArray();
    static int Result(byte[] b) => BitConverter.ToUInt16(b, 2);

    [Fact]
    public async Task A_wave_run_offers_buffs_counts_points_and_pays_at_the_deadline()
    {
        var store = new FakeCharacterStore { Progress = (30, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 30);
        await session.TickAsync(Now + 10);
        var t0 = Now + 1000;
        var inside = await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 900001, t0);
        var prepare = inside.First(b => Op(b) == GameSession.DungeonStateNt);
        Assert.Equal(GameSession.DungeonStatePrepare, prepare[6]);
        var info = inside.First(b => Op(b) == DailyDungeons.DungeonBuffInfoNt);
        Assert.Equal(inside.IndexOf(prepare) + 1, inside.IndexOf(info));
        Assert.Equal(7 + 3 * 8, info.Length);

        var wave = DailyDungeons.Embedded.Of(900001)!;
        var chosen = wave.BuffSlots[2].First(b => b.Id == BitConverter.ToInt32(info, 11 + 8));
        var picked = await session.HandleAsync(Pick(900001, 2), t0 + 100);
        Assert.Equal(DailyDungeons.DungeonBuffSelectReply(0, 900001, 2), picked[0]);
        Assert.Contains(session.BuffWires(), w => w.AbnormalId == chosen.AbnormalId);
        Assert.Equal(GameSession.DungeonBuffInvalidData, Result((await session.HandleAsync(Pick(900001, 9), t0 + 110))[0]));
        Assert.Equal(GameSession.DungeonBuffNotInMap, Result((await session.HandleAsync(Pick(900002, 1), t0 + 120))[0]));

        var processAt = session.Copy!.ProcessAtMs;
        var started = await session.TickAsync(processAt + 1);
        var state = started.First(b => Op(b) == GameSession.DungeonStateNt);
        Assert.Equal(GameSession.DungeonStateProcess, state[6]);
        Assert.Contains(started, b => b.SequenceEqual(DailyDungeons.DungeonPoint(900001, 0)));
        Assert.Equal(GameSession.DungeonBuffNotPreparing, Result((await session.HandleAsync(Pick(900001, 1), processAt + 10))[0]));

        session.DailyKilledForTests(2701427, processAt + 1000);   // an elite: 1000 points
        session.DailyKilledForTests(2701415, processAt + 1100);   // a normal monster: 10
        Assert.Equal(1010, session.Copy.Points);

        var deadline = session.Copy.DeadlineMs;
        session.DailyKilledForTests(2701427, deadline + 1);   // after the time: nothing
        var end = await session.TickAsync(deadline + 2);
        var tier = DailyDungeons.Reward(wave, 1, 1010)!;
        Assert.Contains(end, b => b.SequenceEqual(DailyDungeons.WaveDungeonCompleteResult(900001, 1, 90_000, 1010, 0, tier.Items)));
        Assert.Contains(end, b => b.SequenceEqual(GameSession.DungeonCompleteResult(900001, true, tier.Items)));
        Assert.Contains(end, b => Op(b) == GameSession.DungeonStateNt && b[6] == GameSession.DungeonStateComplete);
        Assert.Equal(new DailyRecord(900001, 1, 90, 1010), session.DailyRecordsForChecks[900001]);
        Assert.Contains(end, b => b.SequenceEqual(DailyDungeons.WaveDungeonInfo([new DailyRecord(900001, 1, 90, 1010)])));
        await session.TickAsync(deadline + 3000);
        Assert.True(store.Settings.ContainsKey("daily_dungeon_records"));
    }

    [Fact]
    public void The_buff_abnormals_carry_stats_and_the_daily_maps_have_waves()
    {
        Assert.Contains(AbnormalSpecs.Info(19003001)!.Effects, e => e.Type == "StatChange" && e.Values[0] == "DamageRatio" && e.Values[1] == "3000");
        Assert.Contains(AbnormalSpecs.Info(19003041)!.Effects, e => e.Type == "StatChange" && e.Values[0] == "MoveSpeed");
        var wave = DailyDungeons.Embedded.Of(900001)!;
        Assert.All(wave.BuffSlots.Values.SelectMany(v => v), b => Assert.NotNull(AbnormalSpecs.Info(b.AbnormalId)));
        Assert.Equal(10, GameSession.DailyWaves(900001)["DungeonWave_0001_Wave_L_B_03"].Length);
        Assert.Equal("Dungeon_Daily_0001_Single_OrcusGem", GameSession.DailyWaves(900002)["Wave_Dungeon_Daily_0001"][0]);
        Assert.Empty(GameSession.DailyWaves(1));
    }
}
