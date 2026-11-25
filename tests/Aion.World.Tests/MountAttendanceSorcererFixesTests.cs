using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MountAttendanceSorcererFixesTests
{
    const long Now = 1_795_350_000_000; // 2026-11-22 UTC — global attendance board 1025 open
    const int PetRide = 1002;
    const int FlameArrow = 15210000;

    [Fact]
    public async Task Mount_auto_acquires_when_souls_cover_need_soul()
    {
        var store = new FakeCharacterStore();
        store.RideRows.Add(new Application.Gameplay.RideRecord(PetRide, 1, 5, Owned: false));
        store.DefaultRide = PetRide;
        store.Progress = (11, 0);
        var session = new GameSession(GameSessionTestHelpers.LiveGameData, new GameSessionOptions { CharacterLevel = 11, WorldId = store.WorldId }, new Random(7))
        {
            Store = store
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);

        var mounted = await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 2);
        var rs = Assert.Single(mounted, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOnRs);
        var reader = new PacketReader(rs) { Offset = 2 };
        Assert.Equal(session.PlayerKey, (int)reader.Varint());
        Assert.Equal(RidePackets.OnSuccess, reader.U16());
        Assert.Equal(PetRide, session.MountedRide);
        Assert.True(store.RideRows.Single(r => r.RideId == PetRide).Owned);
    }

    [Fact]
    public async Task Attendance_collect_claims_every_pending_step_in_one_request()
    {
        var store = new FakeCharacterStore();
        var board = GameSession.LatestAttendanceSeasons(1, Now).First().Board;
        store.Achievements.Add(new Application.Gameplay.AchievementRecord(board.Achievement, 3, null, 0, DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 11);
        var reply = await session.HandleAsync(
            GameSessionTestHelpers.AttendanceBatch(GameSessionTestHelpers.AttendanceGroupId(board.Achievement)), Now);
        Assert.Equal(0, BitConverter.ToInt32(Assert.Single(reply, p => GameSessionTestHelpers.Op(p) == GameSession.TakeAchievementRewardBatchRs), 2));
        Assert.Equal(3, Assert.Single(store.Achievements, a => a.Id == board.Achievement).RewardedStep);
        Assert.Contains(reply, p => GameSessionTestHelpers.Op(p) == 0x561b);
        // The batch checks every open board in first (one 0x5684 per board) and then carries the claimed final step.
        var updates = reply.Where(p => GameSessionTestHelpers.Op(p) == GameSession.AchievementCountUpdateNt).ToList();
        Assert.NotEmpty(updates);
        Assert.Contains(updates, p => AchievementStep(p, board.Achievement) == 3);
    }

    static int? AchievementStep(byte[] packet, int achievementId)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        var count = checked((int)reader.Varint());
        for (var i = 0; i < count; i++)
        {
            var id = reader.I32();
            _ = reader.I32();       // progress count
            _ = reader.I64();       // complete time
            var step = reader.U8();
            if (id == achievementId) return step;
        }
        return null;
    }

    [Fact]
    public async Task Flame_arrow_restores_mp_on_hit_via_skill_effect()
    {
        var (session, mob) = await SkillEffectTests.Setup("Sorcerer");
        session.SetResource('M', 100);
        var mpBefore = session.Resource!.Mp;
        var at = session.Position!.Value;
        var cast = new PacketWriter().U16(0x3800).U16(16).I32(FlameArrow).U8(2).Varint(mob.Key).F32(0).Vec3(at).U8(9).ToArray();
        await session.HandleAsync(cast, Now + 6_000);
        var landed = await session.TickAsync(Now + 6_000 + 2_000);
        Assert.True(session.Resource.Mp > mpBefore);
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x8d00);
    }
}