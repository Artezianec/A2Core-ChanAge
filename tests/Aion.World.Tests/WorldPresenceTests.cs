using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class WorldPresenceTests
{
    [Fact]
    public void RealPlayers_excludes_playerbots()
    {
        var presence = new WorldPresence();
        var human = new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(1)) { Bot = false };
        var bot = new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(2)) { Bot = true };
        presence.Enter("Hero", human, Guid.NewGuid());
        presence.Enter("AltBot", bot, Guid.NewGuid());
        Assert.Equal(2, presence.All.Count);
        Assert.Equal(1, presence.RealPlayers);
    }

    [Fact]
    public void RealPlayers_is_zero_when_empty() => Assert.Equal(0, new WorldPresence().RealPlayers);
}