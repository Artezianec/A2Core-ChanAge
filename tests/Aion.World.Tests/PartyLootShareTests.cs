using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class PartyLootShareTests
{
    const long Now = 1_761_400_000_000;
    static readonly GameData Data = GameData.Embedded;

    [Fact]
    public void Party_next_looter_cycles_join_order()
    {
        var lead = new WorldPlayer(null!, 1) { Appear = () => [] };
        var mid = new WorldPlayer(null!, 2) { Appear = () => [] };
        var last = new WorldPlayer(null!, 3) { Appear = () => [] };
        var party = new Party(1, lead);
        party.Members.Add(mid);
        party.Members.Add(last);
        var eligible = new WorldPlayer[] { lead, mid, last };
        Assert.Same(lead, party.NextLooter(eligible));
        Assert.Same(mid, party.NextLooter(eligible));
        Assert.Same(last, party.NextLooter(eligible));
        Assert.Same(lead, party.NextLooter(eligible));
    }

    [Fact]
    public async Task Round_robin_assigns_items_in_join_order()
    {
        GameSession.ConfigureWorldRules(new WorldRules { PartyLoot = "roundrobin" });
        var world = new SharedWorld(Data);
        var storeMid = new FakeCharacterStore();
        var storeLast = new FakeCharacterStore();
        var lead = await EnterPartyWorld(world, new FakeCharacterStore(), "Lead", 1);
        var mid = await EnterPartyWorld(world, storeMid, "Mid", 2);
        var last = await EnterPartyWorld(world, storeLast, "Last", 3);
        var party = new Party(1, lead.WorldPlayer!);
        party.Members.Add(mid.WorldPlayer!);
        party.Members.Add(last.WorldPlayer!);
        world.Social.Parties[party.Key] = party;
        var spot = new Vector3d(-127874, -111187, 25393);
        foreach (var session in new[] { lead, mid, last })
        {
            session.Teleport(1010, spot, Now);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        }
        var items = new List<(int, long)> { (110150028, 1), (110150035, 1), (110160001, 1) };
        var kept = lead.ShareLootWithParty(items, 0);
        Assert.Single(kept);
        Assert.Equal(110150028, kept[0].ItemId);
        await mid.TickAsync(Now + 1);
        await last.TickAsync(Now + 1);
        Assert.Contains(storeMid.Items.Where(i => i.Location == "bag"), i => i.TemplateId == 110150035);
        Assert.Contains(storeLast.Items.Where(i => i.Location == "bag"), i => i.TemplateId == 110160001);
    }

    [Fact]
    public async Task Kinah_splits_evenly_among_nearby_party()
    {
        GameSession.ConfigureWorldRules(new WorldRules { PartyLoot = "roundrobin" });
        var world = new SharedWorld(Data);
        var storeAlly = new FakeCharacterStore();
        var lead = await EnterPartyWorld(world, new FakeCharacterStore(), "KLead", 4);
        var ally = await EnterPartyWorld(world, storeAlly, "KAlly", 5);
        var party = new Party(2, lead.WorldPlayer!);
        party.Members.Add(ally.WorldPlayer!);
        world.Social.Parties[party.Key] = party;
        var spot = new Vector3d(-127874, -111187, 25393);
        lead.Teleport(1010, spot, Now);
        ally.Teleport(1010, spot, Now);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await ally.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var kept = lead.ShareLootWithParty([], 101);
        Assert.Single(kept);
        Assert.Equal(GameSession.KinaItemId, kept[0].ItemId);
        Assert.Equal(51, kept[0].Count);
        await ally.TickAsync(Now + 1);
        var allyKina = storeAlly.Items.Where(i => i.TemplateId == GameSession.KinaItemId && i.Location == "bag").Sum(i => i.Quantity);
        Assert.Equal(50, allyKina);
    }

    [Fact]
    public async Task Killer_rule_keeps_everything_on_killer()
    {
        GameSession.ConfigureWorldRules(new WorldRules { PartyLoot = "killer" });
        var world = new SharedWorld(Data);
        var lead = await EnterPartyWorld(world, new FakeCharacterStore(), "SoloK", 6);
        var ally = await EnterPartyWorld(world, new FakeCharacterStore(), "Far", 7);
        var party = new Party(3, lead.WorldPlayer!);
        party.Members.Add(ally.WorldPlayer!);
        world.Social.Parties[party.Key] = party;
        var kept = lead.ShareLootWithParty([(110150028, 1), (110150035, 1)], 40);
        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void WorldRules_defaults_are_off_for_prod()
    {
        Assert.Null(WorldRules.Default.LootRule);
        Assert.False(WorldRules.Default.FallbackLoot);
        Assert.False(WorldRules.Default.MonsterKinahDrops);
        Assert.False(WorldRules.Default.SharesPartyLoot);
        Assert.Throws<InvalidOperationException>(() => new WorldRules { PartyLoot = "invalid" }.Validated());
    }

    [Fact]
    public void Monster_kinah_uses_server_rates_kinah_multiplier()
    {
        var rates = new RatesOptions { Kinah = 2 }.Resolve();
        Assert.Equal(20, rates.KinahAmount(10));
        var scaled = rates.WithKinah([(GameSession.KinaItemId, 5), (110150028, 1)]);
        Assert.Equal(10, scaled.First(i => i.ItemId == GameSession.KinaItemId).Count);
    }

    static async Task<GameSession> EnterPartyWorld(SharedWorld world, FakeCharacterStore store, string name, int wireSuffix)
    {
        var session = new GameSession(Data,
            new GameSessionOptions { World = world, CharacterLevel = 15, WorldId = store.WorldId, TrainingDummy = true },
            new Random(wireSuffix))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }
}