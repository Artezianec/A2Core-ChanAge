using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Manastone imprinting (GameSession.Imprint.cs; adapted from Addiboii/A2Core cfd5b67): stone 531360001 on the two-slot Magicbook
/// 110550046 for 1000 Kinah, as the community recording 2026-11-06 234912 (i49411-49417); the reply is rebuilt byte for byte.
/// </summary>
public sealed class ImprintTests
{
    const long Now = 1_760_003_000_000;
    const int Stone = 531360001, Magicbook = 110550046;
    const long BookUid = 0x6a0000092440, StoneUid = 0xdd0000078064;
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    static byte[] Imprint(long equip, int stone, long stoneUid) => new PacketWriter().U16(GameSession.ItemImprintRq).I64(equip).I32(stone).I64(stoneUid).ToArray();
    static int Result(byte[] packet) => BitConverter.ToUInt16(packet, 2);

    [Fact]
    public void Tables_read_the_recorded_stone_and_item()
    {
        var tables = ManastoneTables.Embedded;
        Assert.Equal(2, tables.Item(Magicbook)!.Count);
        Assert.Equal(1000, tables.Item(Magicbook)!.Kina);
        Assert.Equal(("ms_10_common_normal", "Magicstone_Group_Equip"), tables.Stone(Stone));
        Assert.Equal(30, tables.Amount(Stone, 199, 2));
        Assert.Equal(10, tables.Amount(Stone, 307, 1));
    }

    [Fact]
    public async Task Imprint_rolls_every_slot_spends_the_stone_and_kina_and_counts_when_worn()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        ItemState Add(int template, long count, long wire)
        {
            var item = new ItemState(Guid.NewGuid(), "b", template, count, "bag", null, 1, Empty, Empty, wire);
            store.Items.Add(item);
            return item;
        }
        var book = Add(Magicbook, 1, BookUid);
        var stones = Add(Stone, 8, StoneUid);
        store.Items.RemoveAll(i => i.TemplateId == GameSession.KinaItemId);
        Add(GameSession.KinaItemId, 1500, 0x7000);
        await session.InventoryChangesAsync(default);

        Assert.Equal(0x034f, Result(Assert.Single(await session.HandleAsync(Imprint(StoneUid, Stone, StoneUid), Now))));
        Assert.Equal(0x0350, Result(Assert.Single(await session.HandleAsync(Imprint(BookUid, Stone + 1, StoneUid), Now))));

        // The recorded rolls: MPMax at grade 2, then ArmorDefense at grade 1.
        var draws = new Queue<int>([9000, 8000, 1500, 0]);
        session.ImprintRoll = _ => draws.Dequeue();
        var done = await session.HandleAsync(Imprint(BookUid, Stone, StoneUid), Now);
        Assert.Equal("2a56000040240900006a000001e9ab1f6480070000dd00000201e9ab1fc7000201e9ab1f330101", Convert.ToHexString(done[^1]).ToLowerInvariant());
        Assert.Equal(7, store.Items.Single(i => i.InstanceId == stones.InstanceId).Quantity);
        Assert.Equal(500, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        Assert.Equal("[[531360001,199,2],[531360001,307,1]]", store.Items.Single(i => i.InstanceId == book.InstanceId).Upgrades.GetProperty("magicstones").GetRawText());
        var change = done.Single(b => GameSessionTestHelpers.Op(b) == 0x561b);
        Assert.True(change.AsSpan().IndexOf(Convert.FromHexString("0201e9ab1fc7000201e9ab1f330101")) > 0, "the item detail carries its two slots");

        Assert.Equal(0x0107, Result(Assert.Single(await session.HandleAsync(Imprint(BookUid, Stone, StoneUid), Now))));
        Assert.Equal(7, store.Items.Single(i => i.InstanceId == stones.InstanceId).Quantity);

        // Worn: the level change first, and the slots' stats count.
        foreach (var i in store.Items.Where(i => i.Location == "equipment" && i.Slot == 1).ToList()) store.Items[store.Items.IndexOf(i)] = i with { Location = "bag", Slot = null };
        var worn = store.Items.FindIndex(i => i.InstanceId == book.InstanceId);
        store.Items[worn] = store.Items[worn] with { Location = "equipment", Slot = 1 };
        Add(GameSession.KinaItemId, 5000, 0x7002);
        await session.InventoryChangesAsync(default);
        var defense = session.Defense;
        draws = new Queue<int>([1500, 8000, 1500, 8000]);   // ArmorDefense at grade 2 on both slots: 30 + 30 instead of 10
        var again = await session.HandleAsync(Imprint(BookUid, Stone, StoneUid), Now);
        Assert.Equal(0x561d, GameSessionTestHelpers.Op(again[0]));
        Assert.Equal(0, Result(again.Single(b => GameSessionTestHelpers.Op(b) == GameSession.ItemImprintRs)));
        Assert.Equal(defense - 10 + 60, session.Defense);
    }
}
