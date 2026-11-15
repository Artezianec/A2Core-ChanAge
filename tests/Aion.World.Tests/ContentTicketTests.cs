using Aion.Protocol.Native;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

/// <summary>Entry tickets (GameSession.ContentTickets.cs), ported from Addiboii/A2Core 87afb91, feadae2 and 894105a.</summary>
public sealed class ContentTicketTests
{
    const long Now = 1_793_368_800_000;

    static long Ms(int day, int hour) => new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Tables_read_the_client_tickets_and_their_recharges()
    {
        var tickets = ContentTicketTables.Embedded;
        Assert.Equal(73, tickets.All.Count);
        var awaken = tickets.Of(80000001)!;   // Contents_Ticket_Awaken: 3, +3 on Wednesday at 57600 s
        Assert.True(awaken is { Counted: true, Max: 3, Weekly: [3, 57600, 3] });
        Assert.True(tickets.UsesOf(910011).Single() is { TicketId: 80000001, Count: 1, Deduct: "Enter" });
        Assert.Equal(3, ContentTicketTables.Recharged(awaken, 0, Ms(27, 12), Ms(29, 12)));
        Assert.Equal(0, ContentTicketTables.Recharged(awaken, 0, Ms(28, 17), Ms(29, 12)));
        Assert.Equal(3, ContentTicketTables.Recharged(awaken, 1, Ms(20, 12), Ms(29, 12)));
        Assert.Equal(6, ContentTicketTables.Recharged(tickets.Of(1)!, 0, Ms(27, 12), Ms(28, 17)));      // daily +3 at 16:00, twice
        Assert.Equal(2, ContentTicketTables.Recharged(tickets.Of(60000006)!, 0, Ms(27, 0), Ms(27, 16))); // +1 every 8 hours
        Assert.True(tickets.Of(3) is { Counted: false });   // Time tickets (the Abyss) are not counted
        Assert.Equal(("reward_ticket_PartyDungeon_Common", 20), tickets.CubeCost(600002));   // KraoCave Easy's cube
        Assert.Empty(tickets.UsesOf(600002));
    }

    [Fact]
    public async Task World_entry_lists_the_tickets_as_recorded()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3)) { Store = store };
        var all = new List<byte[]>();
        all.AddRange(await session.HandleAsync(Login(), Now));
        all.AddRange(await session.HandleAsync(LightCreate(), Now));
        all.AddRange(await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now));
        all.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        var list = all.Single(b => Op(b) == GameSession.ContentsTicketsNt);
        // Tickets 1-10 byte-exact to the community recording 2026-11-06 084658 #1129 (counts, full Time tickets, empty ones).
        Assert.StartsWith("0b614904010000000c0103000000808580010000000004040000000e000600000004070000001c04080000000701090000008085800100000000040a0000000e",
            Convert.ToHexStringLower(list));
        Assert.DoesNotContain(all, b => Op(b) == 0x3854);   // our old guess
    }

    [Fact]
    public async Task A_ten_minute_mark_sends_the_recharge_update()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now);
        var mark = (Now / 600_000 + 1) * 600_000;
        await session.TickAsync(mark - 61_000);
        var before = await session.TickAsync(mark - 1000);
        var at = await session.TickAsync(mark + 1000);
        Assert.DoesNotContain(before, b => Op(b) == GameSession.ContentsTicketUpdateNt);
        // As recorded 2026-11-04 21:50: 0x610c {00, ticket 10 / 12 at count 14, reason 2 Recharge}.
        Assert.Contains(at, b => Convert.ToHexStringLower(b) == "0c6100040a0000000e02");
        Assert.Contains(at, b => Convert.ToHexStringLower(b) == "0c6100040c0000000e02");
    }
}
