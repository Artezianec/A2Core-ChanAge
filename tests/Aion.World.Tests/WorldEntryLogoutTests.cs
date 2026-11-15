using System.Text.Json;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The recorded world-entry additions and the logout state (adapted from Addiboii/A2Core 827b359, be7c165, 525b531).</summary>
public sealed class WorldEntryLogoutTests
{
    const long Now = 1_760_003_000_000;

    [Fact]
    public void Trust_limit_notice_is_the_recorded_form()
    {
        const string expected = "62e2" + "0704020200000000000000000065cd1d00000000872fb3b0a101000003030000000000000000ffffffffffffffff872fb3b0a1010000"
            + "04040000000000000000ffffffffffffffff872fb3b0a101000005050000000000000000ffffffffffffffff872fb3b0a1010000872fb3b0a101000005000000";
        Assert.Equal(expected, Convert.ToHexString(GameSession.TrustLimitInfoNotice(0x1a1b0b32f87)).ToLowerInvariant());
    }

    [Fact]
    public void Monolith_list_and_arena_matching_follow_the_race()
    {
        Assert.Equal("0be303" + "e903000000000000" + "ea03000000000000" + "b90b000000000000", Convert.ToHexString(GameSession.MonolithList(1)).ToLowerInvariant());
        Assert.Equal("0be303" + "d107000000000000" + "d207000000000000" + "a10f000000000000", Convert.ToHexString(GameSession.MonolithList(2)).ToLowerInvariant());
        Assert.Equal("0a40010102630901650902", Convert.ToHexString(GameSession.ArenaMatchingInfo(2)).ToLowerInvariant());
        Assert.Equal("0a40010102230501290502", Convert.ToHexString(GameSession.ArenaMatchingInfo(1)).ToLowerInvariant());
    }

    static async Task<GameSession> EnterAsync(FakeCharacterStore store, long now)
    {
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Offline").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    static object Row(long expireAt, long? savedAt) => savedAt is { } at
        ? new { Uid = 7, AbnormalId = 22101051, SkillId = 0, ExpireAt = expireAt, Rebirth = false, Polymorph = false, Scripted = true, Relationship = (int?)null, Stats = new Dictionary<string, int>(), Heals = Array.Empty<object>(), Combat = (object?)null, Dots = Array.Empty<object>(), SavedAt = at }
        : new { Uid = 7, AbnormalId = 22101051, SkillId = 0, ExpireAt = expireAt, Rebirth = false, Polymorph = false, Scripted = true, Relationship = (int?)null, Stats = new Dictionary<string, int>(), Heals = Array.Empty<object>(), Combat = (object?)null, Dots = Array.Empty<object>() };

    [Fact]
    public async Task Timed_buff_time_left_does_not_run_offline()
    {
        // Saved with 60 s left, logged out for an hour: still 60 s left at the entry.
        var store = new FakeCharacterStore { Progress = (45, 0) };
        store.Settings["active_buffs"] = JsonSerializer.SerializeToElement(new[] { Row(Now - 3_600_000 + 60_000, Now - 3_600_000) });
        var session = await EnterAsync(store, Now);
        Assert.Contains(22101051, session.ActiveBuffs);
    }

    [Fact]
    public async Task Older_saves_without_the_save_time_keep_the_wall_clock()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        store.Settings["active_buffs"] = JsonSerializer.SerializeToElement(new[] { Row(Now - 3_600_000 + 60_000, null) });
        var session = await EnterAsync(store, Now);
        Assert.DoesNotContain(22101051, session.ActiveBuffs);
    }

    [Fact]
    public async Task Logout_rewrites_the_buff_save_time()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        store.Settings["active_buffs"] = JsonSerializer.SerializeToElement(new[] { Row(Now + 300_000, Now - 1_000) });
        var session = await EnterAsync(store, Now);
        await session.FlushDeferredPersistenceAsync(Now + 50_000, CancellationToken.None);
        var saved = store.Settings["active_buffs"].EnumerateArray().Single();
        Assert.Equal(Now + 50_000, saved.GetProperty("SavedAt").GetInt64());
    }
}
