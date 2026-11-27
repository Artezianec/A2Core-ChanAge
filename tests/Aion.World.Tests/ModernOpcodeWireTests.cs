using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// 2.0.13 handlers whose 2.0.9 opcode is another live message: they must answer with the modern
/// number while 2.0.9 keeps the old one.
/// </summary>
public sealed class ModernOpcodeWireTests
{
    const long Now = 1_760_001_000_000;

    static ushort Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    static WireVersion Pin(WireVersion version)
    {
        var previous = WireContext.Current;
        WireContext.Current = version;
        return previous;
    }

    [Fact]
    public async Task Airspace_battle_schedule_uses_the_2_0_13_opcode_on_modern()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V213);
        try
        {
            var replies = await session.HandleAsync(new byte[] { 0x00, 0x43 }, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0x4301);   // AirspaceBattleNt
            Assert.DoesNotContain(replies, p => Op(p) == 0x372D);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Airspace_battle_schedule_keeps_the_2_0_9_opcode_on_v209()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V209);
        try
        {
            var replies = await session.HandleAsync(new byte[] { 0x2C, 0x37 }, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0x372D);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Abyss_rift_progress_uses_the_2_0_13_opcodes_on_modern()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V213);
        try
        {
            var request = new PacketWriter().U16(0x6302).I32(1).ToArray();
            var replies = await session.HandleAsync(request, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0x6303);   // AbyssRiftProgressInfo_RS
            Assert.DoesNotContain(replies, p => Op(p) == 0x385C);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Combat_state_requests_update_the_session_and_reply_on_2_0_13_opcodes()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V213);
        try
        {
            var on = await session.HandleAsync(new byte[] { 0x3A, 0x38 }, Now + 1);
            Assert.Contains(on, p => Op(p) == 0x383B);
            Assert.True(session.InCombat);

            var off = await session.HandleAsync(new byte[] { 0x3C, 0x38 }, Now + 2);
            Assert.Contains(off, p => Op(p) == 0x383D);
            Assert.False(session.InCombat);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Client_key_binding_info_is_stored_and_never_answered()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var packet = new PacketWriter().U16(0xFFB3).String("main-keys").String("sub-keys").ToArray();
        var replies = await session.HandleAsync(packet, Now + 1);
        Assert.Empty(replies);
        Assert.Equal("main-keys", session.ClientMainKeyBindings);
        Assert.Equal("sub-keys", session.ClientSubKeyBindings);
    }

    [Fact]
    public async Task Two_0_13_select_sends_the_limit_status_entry_extras()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(5))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        var previous = Pin(WireVersion.V213);
        try
        {
            var select = new PacketWriter().U16(0xC4F6).I32(0).I32(0).I32(0)
                .Varint((uint)wireId << 16).I32(0).ToArray();
            var replies = await session.HandleAsync(select, Now + 1);
            Assert.Contains(replies, p => Op(p) == 0xFFA5);
            Assert.Contains(replies, p => Op(p) == 0xFFA7);
            Assert.Contains(replies, p => Op(p) == 0xFFAE);
            Assert.Contains(replies, p => Op(p) == 0x5712);
            Assert.Contains(replies, p => Op(p) == 0x8D83);
            Assert.Contains(replies, p => Op(p) == 0xE34C);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Party_roll_uses_the_2_0_13_opcodes_on_modern_and_2_0_9_on_v209()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 1, level: 55);
        var previous = Pin(WireVersion.V213);
        try
        {
            var modern = await session.HandleAsync(new PacketWriter().U16(0x5722).I64(1).ToArray(), Now + 1);
            Assert.Contains(modern, p => Op(p) == 0x5723);   // RollDice_RS
        }
        finally { WireContext.Current = previous; }

        previous = Pin(WireVersion.V209);
        try
        {
            var legacy = await session.HandleAsync(new PacketWriter().U16(0x381D).I64(2).ToArray(), Now + 2);
            Assert.Contains(legacy, p => Op(p) == 0x381E);
        }
        finally { WireContext.Current = previous; }
    }
}
