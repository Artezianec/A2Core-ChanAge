using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LobbyCreateSlotResolveTests
{
    const long Now = 8_000_000;
    const byte ClientAutoSlot = 8;

    static byte[] CreatePacket(string name, uint creationId, byte slot) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32(creationId).Bytes([0x0c, 0x64]).U8(2).U8(slot).U8(0).ToArray();

    static int CreateResult(IReadOnlyList<byte[]> replies) => BitConverter.ToInt32(replies[0], 2);

    [Fact]
    public async Task Empty_max_slot_account_accepts_client_slot_equals_capacity()
    {
        var store = new FakeCharacterStore { ExtendedCharacterSlots = GameSession.MaxExtendedLobbySlots };
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId, WorldRace = _ => 2 }, new Random(14))
            { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);

        var created = await session.HandleAsync(CreatePacket("ChanterEight", 8, ClientAutoSlot), Now + 1);
        Assert.Equal(0, CreateResult(created));
        var hero = Assert.Single(store.States);
        Assert.Equal("ChanterEight", hero.Summary.Name);
        Assert.Equal(0, hero.LobbySlot);
        var profile = GameData.Embedded.Profile(hero.CreationId, hero.Gender, 2);
        Assert.NotNull(profile);
        Assert.Equal(35, profile!.PcId);
    }

    [Fact]
    public async Task Occupied_requested_slot_assigns_next_free_without_moving_existing()
    {
        var store = new FakeCharacterStore { ExtendedCharacterSlots = GameSession.MaxExtendedLobbySlots };
        store.States.Add(new CharacterState(
            new CharacterSummary(Guid.NewGuid(), store.AccountId, store.WorldId, "Held", 1),
            1, 1, 1, [], new CharacterProgress(1, 0, 1000,
                StoredCharacters.PositionJson(Vector3d.Zero),
                System.Text.Json.JsonSerializer.SerializeToElement(new { })),
            1, WireId: 501, LobbySlot: 1, RegisteredUnixMs: 1_000));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(15)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);

        var created = await session.HandleAsync(CreatePacket("SecondHero", 1, 1), Now + 1);
        Assert.Equal(0, CreateResult(created));
        Assert.Equal(1, store.States.Single(s => s.Summary.Name == "Held").LobbySlot);
        Assert.Equal(0, store.States.Single(s => s.Summary.Name == "SecondHero").LobbySlot);
    }

    [Fact]
    public void Resolve_create_slot_maps_out_of_range_to_first_free()
    {
        Assert.Equal(0, GameSession.ResolveCreateLobbySlot(8, 8, []));
        Assert.Equal(0, GameSession.ResolveCreateLobbySlot(99, 4, []));
    }

    [Fact]
    public void Resolve_create_slot_returns_null_when_full()
    {
        var account = Guid.NewGuid();
        var full = Enumerable.Range(0, 4).Select(slot => new CharacterState(
            new CharacterSummary(Guid.NewGuid(), account, 1, $"H{slot}", 1),
            1, 1, 1, [], new CharacterProgress(1, 0, 1000,
                StoredCharacters.PositionJson(Vector3d.Zero),
                System.Text.Json.JsonSerializer.SerializeToElement(new { })),
            1, WireId: 10 + slot, LobbySlot: slot, RegisteredUnixMs: slot)).ToList();
        Assert.Null(GameSession.ResolveCreateLobbySlot(8, 4, full));
    }
}