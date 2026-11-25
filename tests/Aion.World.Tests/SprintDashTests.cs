using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Shift sprint (0x3704): official opens with NormalDodge dash; standalone dodge spam still gets 9-byte unlock.</summary>
public sealed class SprintDashTests
{
    static readonly long Now = 1_760_004_000_000;

    static byte[] SprintOn() => new PacketWriter().U16(0x3704).I64(1).ToArray();

    static byte[] DodgeCast() =>
        new PacketWriter().U16(0x3800).U16(16).I32(GameSession.CombatDodge).U8(2).Varint(1).F32(0)
            .Vec3(new Vector3d(100, 0, 0)).U8(9).ToArray();

    static int[] Opcodes(IEnumerable<byte[]> packets) => packets.Select(p => (int)GameSessionTestHelpers.Op(p)).ToArray();

    [Fact]
    public async Task Sprint_on_then_auto_combat_dodge_waits_one_tick_and_sends_the_official_batch()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        Assert.Equal(11, session.DashVariant);
        var spBefore = session.Resource!.Sp;
        var on = await session.HandleAsync(SprintOn(), Now);
        Assert.Empty(on);   // held for the official one-tick answer
        Assert.True(session.HasPendingSprintReply);
        var dash = await session.HandleAsync(DodgeCast(), Now + 4);
        Assert.Empty(dash); // the whole batch waits for the tick, as the official 45-85 ms answer did
        var flush = (await session.TickAsync(Now + 60)).Where(p =>
            GameSessionTestHelpers.Op(p) is 0x8d03 or 0x3741 or 0x3802 or 0x3801 or 0x382a or 0x8d00).ToList();
        Assert.Equal([0x8d03, 0x3741, 0x3802, 0x3801, 0x382a, 0x8d00], Opcodes(flush));
        Assert.False(session.HasPendingSprintReply);
        Assert.Equal(GameSession.NormalDodge, BitConverter.ToInt32(flush[2], 4));
        Assert.Equal(spBefore - GameSession.NormalDodgeSp, session.Resource.Sp);
    }

    [Fact]
    public async Task Late_dash_flush_does_not_append_a_second_sp_update_in_the_same_write()
    {
        // Live 01:32: the tick that flushed the batch ran 110 ms after 0x3704, so the 100 ms sprint tick drained and
        // wrote a second 0x8d00 right after the batch. The official write carries one SP update; the next drain is
        // 100 ms after the batch.
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        await session.HandleAsync(SprintOn(), Now);
        await session.HandleAsync(DodgeCast(), Now + 16);
        var flush = await session.TickAsync(Now + 110);
        Assert.Single(flush, p => GameSessionTestHelpers.Op(p) == 0x8d00);
        var spAfterDash = session.Resource!.Sp;
        Assert.DoesNotContain(await session.TickAsync(Now + 150), p => GameSessionTestHelpers.Op(p) == 0x8d00);
        var drain = await session.TickAsync(Now + 220);
        var sp = Assert.Single(drain, p => GameSessionTestHelpers.Op(p) == 0x8d00);
        Assert.Equal(spAfterDash - GameSession.SprintDrain, SpOf(sp));
    }

    static int SpOf(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        _ = reader.Varint();          // key
        var mask = reader.U8();
        Assert.True((mask & 1) != 0);
        var count = checked((int)reader.Varint());
        for (var i = 0; i < count; i++)
        {
            var type = reader.U8();
            var value = reader.I32();
            if (type == (byte)CharPoint.Sp) return value;
        }
        throw new Xunit.Sdk.XunitException("no SP entry");
    }

    [Fact]
    public async Task Variant_11_reasserts_the_sprint_when_the_roll_cancels_it()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        session.DashVariant = 11;
        await session.HandleAsync(SprintOn(), Now);
        await session.HandleAsync(DodgeCast(), Now + 4);
        var flush = await session.TickAsync(Now + 60);
        Assert.Equal([0x8d03, 0x3741, 0x3802, 0x3801, 0x382a, 0x8d00], Opcodes(flush.Where(p =>
            GameSessionTestHelpers.Op(p) is 0x8d03 or 0x3741 or 0x3802 or 0x3801 or 0x382a or 0x8d00)));
        var off = await session.HandleAsync(new PacketWriter().U16(0x3705).I64(1).ToArray(), Now + 80);
        Assert.DoesNotContain(off, p => GameSessionTestHelpers.Op(p) is 0x3742 or 0x3722);
        Assert.Contains(off, p => GameSessionTestHelpers.Op(p) == 0x3741);
        Assert.True(session.IsSprinting);
        // Only one re-assert: a second off ends the sprint even inside the window.
        var second = await session.HandleAsync(new PacketWriter().U16(0x3705).I64(1).ToArray(), Now + 120);
        Assert.Contains(second, p => GameSessionTestHelpers.Op(p) == 0x3742);
        Assert.False(session.IsSprinting);
    }

    [Fact]
    public async Task Builder_combat_state_writes_the_official_0x8d21_form()
    {
        // Live stance test: force combat off and confirm the packet is key | 00 | state.
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var on = session.SetBuilderCombatState(true, Now);
        Assert.Contains(on, p => p.SequenceEqual(GamePackets.CombatState(session.PlayerKey, true)));
        var off = session.SetBuilderCombatState(false, Now + 1);
        var packet = Assert.Single(off);
        Assert.Equal(GamePackets.CombatState(session.PlayerKey, false), packet);
        Assert.Equal(new byte[] { 0x21, 0x8d }, packet[..2]);
        Assert.Equal(0, packet[^1]);
    }

    [Fact]
    public void Dodge_result_matches_the_official_flags_first_wire()
    {
        // Official capture 20261124-130131 #1418: 01 38 | 00 (flags) | 00 00 (result) | 24 D9 A7 00 (CombatDodge).
        Assert.Equal(Convert.FromHexString("013800000024d9a700"), GamePackets.DodgeResult(GameSession.CombatDodge));
        Assert.Equal(Convert.FromHexString("013800010024d9a700"), GamePackets.DodgeResult(GameSession.CombatDodge, 1));
    }

    [Fact]
    public async Task Sprint_on_then_dash_keeps_the_legacy_variant_0_reply_for_live_tests()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        session.DashVariant = 0;
        Assert.Equal(0, session.DashVariant);
        var on = await session.HandleAsync(SprintOn(), Now);
        Assert.Equal([0x8d03, 0x3741], Opcodes(on));
        var dash = await session.HandleAsync(DodgeCast(), Now + 4);
        Assert.Equal([0x3801, 0x8d00, 0x3802], Opcodes(dash));
    }

    [Fact]
    public async Task Sprint_auto_dodge_with_low_sp_still_sends_dodge_unlock_refusal()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        session.SetResource('S', GameSession.NormalDodgeSp - 1);
        session.DashVariant = 7;
        var spBefore = session.Resource!.Sp;
        await session.HandleAsync(SprintOn(), Now);
        var dash = await session.HandleAsync(DodgeCast(), Now + 4);
        Assert.Equal([0x8d03, 0x3741, 0x3801], Opcodes(dash));
        Assert.Equal(GamePackets.DodgeResult(GameSession.CombatDodge, 1), dash[^1]);
        Assert.Equal(spBefore, session.Resource.Sp);
    }

    [Fact]
    public async Task Rapid_standalone_dodge_resends_while_dodging_are_ignored_not_refused()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var first = await session.HandleAsync(DodgeCast(), Now);
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0x3802);
        for (var i = 1; i < 8; i++)
        {
            var retry = await session.HandleAsync(DodgeCast(), Now + i * 4);
            Assert.Empty(retry); // the 9-byte refusal mid-dash froze the client (live 2026-11-02)
        }
    }

    [Fact]
    public async Task Variant_7_batches_the_sprint_with_the_dash_like_the_official_capture()
    {
        // Official 2026-11-24 06:02:00.802 (capture 20261124-130131 #1415-1421), all in one write:
        // 0x8d03, 0x3741, 0x3802, 0x3801, 0x382a, 0x8d00.
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        session.DashVariant = 7;
        var on = await session.HandleAsync(SprintOn(), Now);
        Assert.Empty(on);
        var dash = await session.HandleAsync(DodgeCast(), Now + 4);
        Assert.Equal([0x8d03, 0x3741, 0x3802, 0x3801, 0x382a, 0x8d00], Opcodes(dash));
    }

    [Fact]
    public async Task Legacy_dash_variant_10_still_accepts_sprint_opening_dodge_not_refusal()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        session.DashVariant = 10;
        await session.HandleAsync(SprintOn(), Now);
        var dash = await session.HandleAsync(DodgeCast(), Now + 4);
        Assert.Equal([0x3801, 0x8d00, 0x3802], Opcodes(dash));
    }
    [Fact]
    public void Live_tuning_file_overrides_the_dash_variant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aion-live-tuning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dash-variant.txt");
            File.WriteAllText(path, "11\n");
            Assert.Equal(11, LiveTuning.Read(path));
            File.WriteAllText(path, "0");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Assert.Equal(0, LiveTuning.Read(path));
            File.WriteAllText(path, "99");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
            Assert.Null(LiveTuning.Read(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

}
