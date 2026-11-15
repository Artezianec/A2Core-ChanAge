using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The CharDetail point lists of 0x3633 (official 2026-11-12: spent 382 / 34, from items 180 / 5; from Doxylamin/KiskEmu 31e71b2).</summary>
public sealed class SpawnPointListTests
{
    [Fact]
    public void My_char_appear_carries_spent_and_item_skill_points()
    {
        var bytes = GamePackets.MyCharAppear(new MyCharacterAppear { Ascension = 5, SpentSkillPoints = 382, SpentStigmaPoints = 34, ItemSkillPoints = 180, ItemStigmaPoints = 5 });
        var expected = Convert.FromHexString("05" + "03017e01022200030000" + "0301b400020500030000");
        Assert.True(bytes.AsSpan().IndexOf(expected) > 0, "ascension byte followed by the spent and item point lists");
    }
}
