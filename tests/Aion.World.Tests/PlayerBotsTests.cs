using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Playerbots (docs/PLAYERBOTS.md; ported from ckrealm/A2Core): summoning one of the owner's characters, following, assisting
/// with the class rotation through the normal cast handler, caps, commands, dismissing, //bot gating and the owner leaving.
/// </summary>
public sealed class PlayerBotsTests
{
    static readonly GameData Data = GameData.Embedded;
    const long Start = 1_700_002_000_000;
    const int KeenStrike = 11020000;

    static byte[] Login() => GameSessionTestHelpers.Login();

    static byte[] Create(string name, int creation, byte slot) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(slot).U8(0).ToArray();

    static byte[] Select(uint key) => new PacketWriter().U16(0x361f).Varint(key).I32(0).Vec3(Vector3d.Zero).ToArray();

    static byte[] Load() => new PacketWriter().U16(0x3622).I32(1).ToArray();

    static byte[] Cast(int skill, int target, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint(target).F32(0).Vec3(at).U8(9).ToArray();

    static byte[] Run(Vector3d to) => new PacketWriter().U16(0x3701).U8(2).Vec3(to).U16(0).F32(0).U8(1).U64(1).ToArray();

    static int CreationOf(string cls) => Enumerable.Range(1, 64).First(c => Data.Profile(c, 1)?.Class == cls);

    static double Apart(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static int KeyOf(byte[] packet)
    {
        var reader = new PacketReader(packet);
        reader.U16();
        return (int)reader.Varint();
    }

    sealed class Scene
    {
        public required SharedWorld World { get; init; }
        public required FakeCharacterStore Store { get; init; }
        public required GameSession Leader { get; init; }
        public required GameSession Watcher { get; init; }
        public long T;
    }

    /// <summary>A stored leader with three more characters (Alt, Ranged, Third) and a development player watching.</summary>
    static async Task<Scene> SceneAsync()
    {
        var world = new SharedWorld(Data);
        var store = new FakeCharacterStore();
        var options = new GameSessionOptions { World = world, TrainingDummy = true, WorldId = store.WorldId };
        long t = Start;
        var leader = new GameSession(Data, options, new Random(21)) { Store = store };
        await leader.HandleAsync(Login(), t);
        await leader.HandleAsync(Create("Leader", 1, 1), t);
        await leader.HandleAsync(Create("Alt", 1, 2), t);
        await leader.HandleAsync(Create("Ranged", CreationOf("Ranger"), 3), t);
        await leader.HandleAsync(Create("Third", 1, 4), t);
        await leader.HandleAsync(Select((uint)store.States[0].WireId << 16), t);
        await leader.HandleAsync(Load(), t);
        var watcher = new GameSession(Data, options, new Random(22));
        await watcher.HandleAsync(Login(), t);
        await watcher.HandleAsync(Create("Watcher", 1, 1), t);
        await watcher.HandleAsync(Select(65536), t);
        await watcher.HandleAsync(Load(), t);
        // A development character starts elsewhere on the map: the watcher stands by the leader.
        var near = leader.WorldPosition!.Value;
        watcher.Teleport(leader.MapId!.Value, near with { X = near.X + 300 }, t);
        await watcher.HandleAsync(Load(), t);
        await watcher.TickAsync(t);
        Assert.True(leader.InWorld, $"leader {store.States.Count} {string.Join(",", store.States.Select(x => x.Summary.Name))}");
        Assert.Equal("Leader", leader.CharacterName);
        Assert.True(watcher.InWorld, "watcher");
        return new Scene { World = world, Store = store, Leader = leader, Watcher = watcher, T = t };
    }

    [Fact]
    public async Task Off_by_default_and_the_host_options_validate()
    {
        var s = await SceneAsync();
        var off = new PlayerBots(Data, new BotOptions(), s.World);
        Assert.Contains("off", await off.CommandAsync(s.Leader, "add Alt", s.T, default));
        Assert.Empty(off.All);
        Assert.False(new BotOptions().Enabled);
        Assert.False(new BotOptions().PlayersMayUse);
        Assert.Throws<InvalidOperationException>(() => new BotOptions { MaxTotal = -1 }.Validated());
        Assert.Throws<InvalidOperationException>(() => new BotOptions { TeleportBeyond = 10 }.Validated());
    }

    [Fact]
    public async Task A_bot_logs_in_joins_the_party_follows_and_assists_with_accepted_casts()
    {
        var s = await SceneAsync();
        var presence = new WorldPresence();
        var bots = new PlayerBots(Data, new BotOptions { Enabled = true, MaxPerAccount = 2, MaxTotal = 3 }, s.World, presence: presence);
        Assert.Contains("yourself", await bots.CommandAsync(s.Leader, "add Leader", s.T, default));
        Assert.Contains("not one of your characters", await bots.CommandAsync(s.Leader, "add Nobody", s.T, default));
        Assert.Equal("Summoning Alt.", await bots.CommandAsync(s.Leader, "add Alt", s.T, default));
        Assert.Equal(PlayerBot.Phase.Pending, bots.All.Single().State);

        await s.Watcher.TickAsync(s.T);
        await bots.TickAsync(s.T += 50);
        var alt = bots.All.Single();
        var altKey = alt.Session.PlayerKey;
        Assert.Equal(PlayerBot.Phase.InWorld, alt.State);
        Assert.True(alt.Session.InWorld && alt.Session.Bot && s.World.Online.ContainsKey(altKey));
        Assert.Equal("Alt", alt.Session.CharacterName);
        Assert.Same(alt.Session, presence.Find("Alt")?.Session);
        // Others see it through the shared world's own paths, and it joined the leader's party.
        var seen = await s.Watcher.TickAsync(s.T);
        Assert.True(seen.Any(b => GameSessionTestHelpers.Op(b) == 0x3645 && KeyOf(b) == altKey),
            "watcher saw: " + string.Join(" ", seen.Select(b => $"{GameSessionTestHelpers.Op(b):x4}/{KeyOf(b)}")));
        Assert.True(alt.InParty);
        var party = s.World.Social.PartyOf(s.Leader.WorldPlayer!);
        Assert.NotNull(party);
        Assert.Equal(2, party!.Members.Count);
        Assert.Contains("already", await bots.CommandAsync(s.Leader, "add Alt", s.T, default));

        // Follow: the leader runs 1500 cm east; the bot catches up along the ground without a speed reject.
        var origin = s.Leader.WorldPosition!.Value;
        for (var i = 1; i <= 3; i++)
        {
            await s.Leader.HandleAsync(Run(origin with { X = origin.X + 500 * i }), s.T += 500);
            for (var k = 0; k < 10; k++) await bots.TickAsync(s.T + k * 50);
        }
        for (var k = 0; k < 100 && Apart(alt.Session.WorldPosition!.Value, s.Leader.WorldPosition!.Value) > BotBrain.FollowFar; k++)
            await bots.TickAsync(s.T += 50);
        Assert.True(Apart(alt.Session.WorldPosition!.Value, s.Leader.WorldPosition!.Value) <= BotBrain.FollowFar);
        Assert.Equal(0, alt.Session.SpeedRejects);
        Assert.True(alt.Brain!.MovesSent > 2);
        Assert.Contains(await s.Watcher.TickAsync(s.T), b => GameSessionTestHelpers.Op(b) == 0x371c && KeyOf(b) == altKey);

        // Assist: the leader hits the training dummy; the bot casts its rotation at it through the session's cast handler.
        var dummy = s.Leader.Targets[Combat.TrainingDummyKey];
        s.Leader.Teleport(s.Leader.MapId!.Value, dummy.Position with { X = dummy.Position.X + 200 }, s.T);
        await s.Leader.HandleAsync(Load(), s.T);
        for (var k = 0; k < 100 && Apart(alt.Session.WorldPosition!.Value, s.Leader.WorldPosition!.Value) > BotBrain.FollowFar; k++)
            await bots.TickAsync(s.T += 50);
        var opening = await s.Leader.HandleAsync(Cast(KeenStrike, dummy.Key, s.Leader.WorldPosition!.Value), s.T += 50);
        Assert.Contains(opening, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Equal(dummy.Key, s.Leader.AttackTarget(s.T)?.Key);
        var hpAfterLeader = dummy.Hp;
        for (var k = 0; k < 240 && !dummy.Dead; k++)
        {
            await bots.TickAsync(s.T += 50);
            if (k % 20 == 0) await s.Leader.HandleAsync(Cast(KeenStrike, dummy.Key, s.Leader.WorldPosition!.Value), s.T);
            await s.Watcher.TickAsync(s.T);
        }
        var casts = alt.Brain.CastLog;
        Assert.True(casts.Count >= 3, $"{casts.Count} casts");
        Assert.True(dummy.Dead || dummy.Hp < hpAfterLeader, $"HP {hpAfterLeader} -> {dummy.Hp}");
        Assert.Equal(alt.Brain.CastsSent, alt.Brain.CastsAccepted);
        Assert.All(casts.Zip(casts.Skip(1)), p => Assert.True(p.Second.At - p.First.At >= BotRotations.Embedded.GlobalCooldownMs));
    }

    [Fact]
    public async Task Caps_commands_dismiss_and_the_owner_leaving()
    {
        var s = await SceneAsync();
        var presence = new WorldPresence();
        var bots = new PlayerBots(Data, new BotOptions { Enabled = true, MaxPerAccount = 2, MaxTotal = 3 }, s.World, presence: presence);
        Assert.Equal("Summoning Alt.", await bots.CommandAsync(s.Leader, "add Alt", s.T, default));
        Assert.Equal("Summoning Ranged.", await bots.CommandAsync(s.Leader, "add Ranged", s.T, default));
        Assert.Contains("MaxPerAccount", await bots.CommandAsync(s.Leader, "add Third", s.T, default));
        await bots.TickAsync(s.T += 50);
        var alt = bots.All.Single(b => b.Name == "Alt");
        var ranged = bots.All.Single(b => b.Name == "Ranged");
        Assert.Equal(PlayerBot.Phase.InWorld, ranged.State);
        Assert.Equal("Ranger", ranged.Session.ClassName);
        Assert.True(ranged.Brain!.Rotation!.Range > 1000);
        Assert.Equal(1, ranged.Brain.Slot);
        // A development login's bot is a new unsaved character of the class asked for; the host cap holds.
        Assert.Equal("Summoning Devbot.", await bots.CommandAsync(s.Watcher, "add Devbot Cleric", s.T, default));
        Assert.Contains("MaxTotal", await bots.CommandAsync(s.Watcher, "add Devbot2", s.T, default));
        await bots.TickAsync(s.T += 50);
        Assert.Equal("Cleric", bots.All.Single(b => b.Name == "Devbot").Session.ClassName);

        Assert.StartsWith("stay", await bots.CommandAsync(s.Leader, "stay", s.T, default));
        Assert.Equal(BotMovement.Stay, alt.Brain!.Movement);
        Assert.StartsWith("passive", await bots.CommandAsync(s.Leader, "passive Alt", s.T, default));
        Assert.False(alt.Brain.Assist);
        Assert.True(ranged.Brain.Assist);
        Assert.StartsWith("role heal", await bots.CommandAsync(s.Leader, "role heal Alt", s.T, default));
        Assert.Equal("heal", alt.Brain.Role);
        Assert.Contains("Alt (InWorld, Stay, passive", await bots.CommandAsync(s.Leader, "list", s.T, default));

        // Dismiss: it leaves the world (others see it go) and the party.
        var altKey = alt.Session.PlayerKey;
        await s.Watcher.TickAsync(s.T);
        Assert.Equal("Dismissed Alt.", bots.Remove(s.Leader, "Alt"));
        await bots.TickAsync(s.T += 50);
        var gone = await s.Watcher.TickAsync(s.T);
        Assert.Equal(PlayerBot.Phase.Gone, alt.State);
        Assert.False(s.World.Online.ContainsKey(altKey));
        Assert.Null(alt.Session.SharedMap);
        Assert.Contains(gone, b => b.SequenceEqual(PlayerPackets.CharDisappear(altKey, 1)));
        Assert.Null(presence.Find("Alt"));
        Assert.DoesNotContain(s.World.Social.PartyOf(s.Leader.WorldPlayer!)?.Members ?? [], m => m.Key == altKey);

        // The owner leaves: its bots go with it; other players' bots stay until their own leader leaves.
        s.Leader.ExitSharedWorld();
        await bots.OwnerLeftAsync(s.Leader);
        Assert.Empty(bots.Of(s.Leader));
        Assert.False(s.World.Online.ContainsKey(ranged.Session.PlayerKey));
        await bots.TickAsync(s.T += 50);
        Assert.Equal("Devbot", bots.All.Single().Name);
        s.Watcher.ExitSharedWorld();
        await bots.TickAsync(s.T += 50);
        Assert.Empty(bots.All);
    }

    [Fact]
    public async Task Bot_command_is_staff_only_unless_players_may_use_it()
    {
        var s = await SceneAsync();
        var presence = new WorldPresence();
        presence.Enter("Leader", s.Leader, s.Store.AccountId);
        var builder = new BuilderCommands(presence, new GmPolicy(), (_, _, _) => { });
        var chat = new Aion.Application.Chat.ChatIdentity("1", "1204", "en_US");
        Assert.Equal("Playerbots are off on this server.", await new GmCommands(presence, builder).HandleAsync(chat, "Leader", "//bot list", default));
        var staffOnly = new PlayerBots(Data, new BotOptions { Enabled = true }, s.World);
        Assert.Equal("Playerbots are for staff on this server.",
            await new GmCommands(presence, builder) { Bots = staffOnly }.HandleAsync(chat, "Leader", "//bot list", default));
        var open = new PlayerBots(Data, new BotOptions { Enabled = true, PlayersMayUse = true }, s.World);
        var listed = new GmCommands(presence, builder) { Bots = open }.HandleAsync(chat, "Leader", "//bot list", default);
        await s.Leader.TickAsync(s.T += 50);   // the command runs on the player's loop
        Assert.StartsWith("No bots", await listed);
    }

    [Fact]
    public async Task A_bot_whose_leaving_throws_is_still_removed_and_the_others_keep_ticking()
    {
        var world = new SharedWorld(Data);
        long t = Start + 500_000;
        var lead = new GameSession(Data, new GameSessionOptions { World = world, TrainingDummy = true }, new Random(41));
        await lead.HandleAsync(Login(), t);
        await lead.HandleAsync(Create("Boss", 1, 1), t);
        await lead.HandleAsync(Select(65536), t);
        await lead.HandleAsync(Load(), t);
        var bots = new PlayerBots(Data, new BotOptions { Enabled = true, MaxPerAccount = 3, MaxTotal = 3 }, world);
        await bots.CommandAsync(lead, "add Broken", t, default);
        await bots.CommandAsync(lead, "add Fine", t, default);
        await bots.TickAsync(t += 50);
        var broken = bots.All.Single(b => b.Name == "Broken");
        var fine = bots.All.Single(b => b.Name == "Fine");
        Assert.Equal(PlayerBot.Phase.InWorld, broken.State);
        Assert.Equal(PlayerBot.Phase.InWorld, fine.State);
        bots.LeaveFault = b => { if (b == broken) throw new InvalidOperationException("leave failed"); };
        bots.Remove(lead, "Broken");
        await bots.TickAsync(t += 50);
        Assert.Equal(PlayerBot.Phase.Gone, broken.State);
        Assert.Equal(nameof(InvalidOperationException), broken.LastError);
        Assert.DoesNotContain(broken, bots.All);
        for (var k = 0; k < 5; k++) await bots.TickAsync(t += 50);
        Assert.Equal(PlayerBot.Phase.InWorld, fine.State);
        Assert.True(world.Online.ContainsKey(fine.Session.PlayerKey));
        bots.LeaveFault = null;
        lead.ExitSharedWorld();
        await bots.OwnerLeftAsync(lead);
    }

    [Fact]
    public async Task A_heal_cleric_bot_heals_its_hurt_leader_and_a_tank_bot_counts_more_hate()
    {
        const int HealingLight = 17100000;
        var world = new SharedWorld(Data);
        long t = Start + 900_000;
        var chief = new GameSession(Data, new GameSessionOptions { World = world, TrainingDummy = true, CharacterLevel = 16 }, new Random(31));
        await chief.HandleAsync(Login(), t);
        await chief.HandleAsync(Create("Chief", 1, 1), t);
        await chief.HandleAsync(Select(65536), t);
        await chief.HandleAsync(Load(), t);
        var squad = new PlayerBots(Data, new BotOptions { Enabled = true, MaxPerAccount = 3, MaxTotal = 5 }, world);
        Assert.Equal("Summoning Medic.", await squad.CommandAsync(chief, "add Medic Cleric", t, default));
        Assert.Equal("Summoning Guard.", await squad.CommandAsync(chief, "add Guard Templar", t, default));
        await squad.TickAsync(t += 50);
        var medic = squad.All.Single(b => b.Name == "Medic");
        var guard = squad.All.Single(b => b.Name == "Guard");
        Assert.All(squad.All, b => Assert.True(b.State == PlayerBot.Phase.InWorld && b.InParty && b.Session.Level == 16));
        Assert.Equal("heal", medic.Brain!.Role);
        Assert.Equal("tank", guard.Brain!.Role);
        async Task Run(int ticks, Func<bool>? until = null)
        {
            for (var k = 0; k < ticks && until?.Invoke() != true; k++)
            {
                await squad.TickAsync(t += 50);
                await chief.TickAsync(t);
            }
        }
        await Run(40);

        chief.SetResource('H', chief.Resource.HpMax * 30 / 100);
        var hurt = chief.Resource.Hp;
        await Run(80, () => medic.Brain.SupportLog.Any(c => c.Skill == HealingLight && c.Target == chief.PlayerKey));
        Assert.Contains(medic.Brain.SupportLog, c => c.Skill == HealingLight && c.Target == chief.PlayerKey);
        await Run(40, () => chief.Resource.Hp > hurt);
        Assert.True(chief.Resource.Hp > hurt, $"HP {hurt} -> {chief.Resource.Hp}");
        Assert.Equal(medic.Brain.CastsSent, medic.Brain.CastsAccepted);

        // The tank's brain sets the bot-rotations.json multiplier on its session; players keep 1.
        Assert.True(BotRotations.Embedded.TankHateMultiplier > 1);
        Assert.Equal(BotRotations.Embedded.TankHateMultiplier, guard.Session.HateMultiplier);
        Assert.Equal(1, medic.Session.HateMultiplier);
        Assert.Equal(1, chief.HateMultiplier);
        chief.ExitSharedWorld();
        await squad.OwnerLeftAsync(chief);
    }
}
