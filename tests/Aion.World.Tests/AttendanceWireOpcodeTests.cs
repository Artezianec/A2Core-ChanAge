using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceWireOpcodeTests
{
    const long Now = 1_760_184_000_000; // 2025-10-11 — open Global attendance boards for Light

    [Fact]
    public async Task Live_e34d_attendance_rq_returns_e34e_with_u16_result_zero()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var reply = await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var rs = Assert.Single(reply, p => GameSessionTestHelpers.Op(p) == GameSession.AttendanceRs);
        Assert.Equal(0xe34e, GameSessionTestHelpers.Op(rs));
        Assert.Equal(4, rs.Length);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Contains(reply, p => GameSessionTestHelpers.Op(p) == GameSession.AchievementCountUpdateNt);
    }

    [Fact]
    public async Task Attendance_check_in_stamps_the_complete_time_like_the_official_rows()
    {
        // Official 0x5683 rows for the open boards: 7/7, 14/14 and 25/28 all carry a non-zero complete time (last check-in).
        // A zero time made the client treat the board as never attended (attendance popup + claim button on every login).
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var reply = await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var updates = reply.Where(p => GameSessionTestHelpers.Op(p) == GameSession.AchievementCountUpdateNt).ToList();
        Assert.NotEmpty(updates);
        var attendance = AchievementTables.Embedded.Boards.Select(b => b.Achievement).ToHashSet();
        var seen = 0;
        foreach (var update in updates)
        {
            var reader = new PacketReader(update) { Offset = 2 };
            var count = checked((int)reader.Varint());
            for (var i = 0; i < count; i++)
            {
                var id = reader.I32();
                _ = reader.I32();       // progress count
                var complete = reader.I64();
                _ = reader.U8();        // rewarded step
                if (!attendance.Contains(id)) continue;
                Assert.NotEqual(0, complete);
                seen++;
            }
        }
        Assert.True(seen > 0);
    }

    [Fact]
    public async Task Stored_attendance_without_a_complete_time_is_stamped_at_load()
    {
        var store = new FakeCharacterStore();
        var board = AchievementTables.Embedded.Boards.First(b => b.Always);
        store.Achievements.Add(new AchievementRecord(board.Achievement, 2, null, 2, DateTimeOffset.FromUnixTimeMilliseconds(Now - 60_000)));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var list = session.AchievementListPacket(Now + 100);
        var reader = new PacketReader(list) { Offset = 2 };
        var count = checked((int)reader.Varint());
        long? complete = null;
        for (var i = 0; i < count; i++)
        {
            var id = reader.I32();
            _ = reader.I32();
            var at = reader.I64();
            _ = reader.U8();
            if (id == board.Achievement) complete = at;
        }
        Assert.Equal(Now - 60_000, complete);
    }

    [Fact]
    public async Task Second_e34d_same_day_still_returns_u16_result_zero_four_bytes()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var again = await session.HandleAsync([0x4d, 0xe3], Now + 101);
        var rs = Assert.Single(again, p => GameSessionTestHelpers.Op(p) == GameSession.AttendanceRs);
        Assert.Equal(4, rs.Length);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == GameSession.AchievementCountUpdateNt);
        Assert.Contains(again, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
    }
}
