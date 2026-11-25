using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: Assassin Shadowstrike 13070000 must jump behind its target and stun it (client Teleport_Caster row + held abnormal).</summary>
public sealed class SkillMoveTests
{
    const int Shadowstrike = 13070000, LeapingSlam = 11190000;
    static readonly long Now = 1_700_000_100_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static async Task<(GameSession Session, Target Mob)> Setup(double direction = 0, int creation = 3)
    {
        var data = GameData.Embedded;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Move").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var dummy = session.Targets![Combat.TrainingDummyKey].Position;
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy with { X = dummy.X + 700 }), Now + 5_000);
        var at = session.Position!.Value;
        var mob = map[88_301] = new Target
        {
            Key = 88_301, Name = "Mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6,
            Position = at with { X = at.X + 600 }, Direction = direction, Radius = 100,
        };
        return (session, mob);
    }

    [Fact]
    public void Shadowstrike_data_carries_the_teleport_row_and_a_held_stun()
    {
        var row = SkillMoves.CasterTeleportFor(Shadowstrike)!;
        Assert.Equal(SkillMoves.TeleportCaster, row[0]);
        Assert.Equal("TargetLocation_TargetDirection", row[1]);
        Assert.Equal(SkillMoves.CasterTeleportFor(Shadowstrike + 5), row);
        Assert.Equal([(100000301, 3000, 3000, true)], SkillAreas.For(Shadowstrike)!.DebuffRows);
        Assert.Null(SkillMoves.CasterTeleportFor(11020000));
    }

    [Fact]
    public void Target_direction_teleport_lands_on_the_targets_back()
    {
        var target = new Vector3d(1000, 2000, 30);
        var caster = new Vector3d(0, 2000, 30);
        var row = new[] { SkillMoves.TeleportCaster, "TargetLocation_TargetDirection", "150", "180", "50" };
        var at = SkillMoves.TeleportDestination(row, caster, target, 0, 100);
        Assert.Equal(target.X - (100 + 150), at.X, 6);
        Assert.Equal(target.Y, at.Y, 6);
        Assert.Equal(target.Z, at.Z, 6);

        // The same row with a target that faces +Y puts its back on -Y.
        var side = SkillMoves.TeleportDestination(row, caster, target, 90, 100);
        Assert.Equal(target.X, side.X, 6);
        Assert.Equal(target.Y - (100 + 150), side.Y, 6);
    }

    [Fact]
    public void Caster_to_target_direction_teleport_lands_on_the_casters_own_side()
    {
        var target = new Vector3d(1000, 2000, 30);
        var caster = new Vector3d(0, 2000, 30);
        var row = new[] { SkillMoves.TeleportCaster, SkillMoves.CasterToTargetLocation, "100", "180", "100" };
        var at = SkillMoves.TeleportDestination(row, caster, target, 90, 100);
        Assert.Equal(target.X - (100 + 100), at.X, 6);
        Assert.Equal(target.Y, at.Y, 6);

        // 0Â° is the far side of the target, along the same approach line.
        var through = SkillMoves.TeleportDestination([row[0], row[1], row[2], "0", row[4]], caster, target, 90, 100);
        Assert.Equal(target.X + (100 + 100), through.X, 6);
        Assert.Equal(target.Y, through.Y, 6);

        // Already inside the landing ring on the caster's side: the caster stays where it is.
        var close = new Vector3d(850, 2000, 30);
        Assert.Equal(close, SkillMoves.TeleportDestination(row, close, target, 0, 100));
    }

    [Fact]
    public void Caster_location_teleport_dashes_from_the_caster_toward_the_target()
    {
        var target = new Vector3d(1000, 2000, 30);
        var caster = new Vector3d(0, 2000, 30);
        var row = new[] { SkillMoves.TeleportCaster, "CasterLocation_CasterToTargetDirection", "400", "180", "400" };
        var at = SkillMoves.TeleportDestination(row, caster, target, 0, 0);
        Assert.Equal(400, at.X, 6);
        Assert.Equal(target.Y, at.Y, 6);
        Assert.Equal(caster.Z, at.Z, 6);
    }

    [Fact]
    public void Facing_is_the_yaw_from_one_point_to_another()
    {
        Assert.Equal(0, SkillMoves.Facing(new Vector3d(0, 0, 0), new Vector3d(5, 0, 0)), 6);
        Assert.Equal(90, SkillMoves.Facing(new Vector3d(0, 0, 0), new Vector3d(0, 5, 0)), 6);
    }

    [Fact]
    public async Task Cast_puts_the_caster_behind_the_target_when_the_first_hit_lands_and_stuns_it()
    {
        var (session, mob) = await Setup(direction: 0);
        var start = session.Position!.Value;
        var replies = await session.HandleAsync(Cast(Shadowstrike, mob.Key, mob.Position), Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Equal(1, session.PendingCasterMoveCount);
        Assert.False(mob.Held);

        var landed = await session.TickAsync(Now + 9_000);

        var row = SkillMoves.CasterTeleportFor(Shadowstrike)!;
        var expected = SkillMoves.TeleportDestination(row, start, mob.Position, mob.Direction, mob.Radius);
        Assert.Equal(0, session.PendingCasterMoveCount);
        Assert.Equal(expected.X, session.WorldPlayer!.Position.X, 3);
        Assert.Equal(expected.Y, session.WorldPlayer.Position.Y, 3);
        Assert.True(session.WorldPlayer.Position.X < mob.Position.X, "behind a target that faces +X is on its -X side");
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);
        Assert.True(mob.Held, "100000301 stuns the monster");
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x382a);
    }

    [Fact]
    public void Leaping_slam_carries_a_target_side_teleport_row()
    {
        var row = SkillMoves.CasterTeleportFor(LeapingSlam)!;
        Assert.Equal(SkillMoves.TeleportCaster, row[0]);
        Assert.Equal(SkillMoves.CasterToTargetLocation, row[1]);
        Assert.Null(SkillMoves.CasterMoveFor(LeapingSlam));
    }

    [Fact]
    public async Task Leaping_slam_lands_in_front_of_its_target_when_the_first_hit_lands()
    {
        var (session, mob) = await Setup(direction: 0, creation: 1);
        var start = session.WorldPlayer!.Position;
        var replies = await session.HandleAsync(Cast(LeapingSlam, mob.Key, mob.Position), Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x372e);
        Assert.Equal(1, session.PendingCasterMoveCount);

        var landed = await session.TickAsync(Now + 9_000);

        var row = SkillMoves.CasterTeleportFor(LeapingSlam)!;
        var expected = SkillMoves.TeleportDestination(row, start, mob.Position, mob.Direction, mob.Radius);
        Assert.Equal(0, session.PendingCasterMoveCount);
        var at = session.WorldPlayer!.Position;
        Assert.Equal(expected.X, at.X, 3);
        Assert.Equal(expected.Y, at.Y, 3);
        Assert.True(at.X > start.X && at.X < mob.Position.X, "the caster stops on its own side of the target");
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);
    }

    [Fact]
    public async Task Target_that_dies_before_the_jump_lands_does_not_teleport_the_caster()
    {
        var (session, mob) = await Setup();
        var start = session.WorldPlayer!.Position;
        await session.HandleAsync(Cast(Shadowstrike, mob.Key, mob.Position), Now + 6_000);
        mob.Hp = 0;
        await session.TickAsync(Now + 9_000);
        Assert.Equal(0, session.PendingCasterMoveCount);
        Assert.Equal(start, session.WorldPlayer.Position);
    }

    [Fact]
    public async Task Skill_without_damage_rows_still_applies_its_abnormal_to_the_caster()
    {
        const int selfBuff = 13260000;
        var (session, _) = await Setup();
        Assert.Empty(GameData.Embedded.Skill(selfBuff)!.Damage);
        Assert.NotNull(SkillAreas.For(selfBuff));
        var replies = await session.HandleAsync(Cast(selfBuff, session.PlayerKey, session.Position!.Value), Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(100200001, session.ActiveBuffs);

        _ = await session.TickAsync(Now + 6_000 + 3_000);
    }
}

