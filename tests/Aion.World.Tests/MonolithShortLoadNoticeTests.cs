using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MonolithShortLoadNoticeTests
{
    const long Now = 1_760_000_500_000;
    const int VerteronEnv = 1200863;

    [Fact]
    public async Task Load_reset_keeps_monolith_notices_enabled_so_short_layer_load_sends_3baa()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 16);
        var objects = WorldObjects.ObjectsOn(1010).ToList();
        var index = objects.FindIndex(o => o.Env == VerteronEnv);
        Assert.True(index >= 0);

        // Same reset a short layer load (no MonolithList_NT resend) runs before ObjectVisibility.
        session.ResetMonolithNoticesForLoad();
        Assert.True(session.MonolithNoticesEnabledForTests);

        var notices = session.MonolithEnvObjNoticesForTests(index, objects[index]);
        Assert.Contains(notices, p => GameSessionTestHelpers.Op(p) == 0x3baa);
    }
}
