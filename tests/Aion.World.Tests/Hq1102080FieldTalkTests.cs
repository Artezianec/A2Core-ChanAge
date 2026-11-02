using Aion.Application.Gameplay;
using Aion.Protocol;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>HQ1102080 Collapsed Barrier Tower: field AskNpc (Hithanya) and hero_verteron_02 HUD sync after step changes.</summary>
public sealed class Hq1102080FieldTalkTests
{
    const long Now = 1_760_006_400_000;
    const int HithanyaNpcId = 2930716;

    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    [Fact]
    public async Task QuestSetStep_emits_part_QuestAdd_with_roll_count_after_progress()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1102080", 1102080, 500, QuestLog.Incomplete, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 14);
        var (_, packets) = await session.QuestCommandAsync("QuestSetStep", "HQ1102080", default, 2);

        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d10));
        var adds = packets.Where(p => Op(p) == 0x8d11).ToList();
        Assert.NotEmpty(adds);
        var body = ReadQuestBody(adds[^1]);
        Assert.Equal(1102080, body.QuestId);
        Assert.Equal(2, body.Step);
        Assert.True(body.RollCount > 0, "episode quest should carry Common_Quest _roll_count on QuestAdd after progress sync");
        Assert.Equal(QuestBook.Embedded.PartRollIndex("HQ1102080"), body.RollCount);
    }

    [Fact]
    public async Task Lock_on_open_field_AskNpc_dialog_when_step_talk_is_open()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1102080", 1102080, 500, QuestLog.Incomplete, 2, new int[6]));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 14);
        session.Teleport(1010, new Vector3d(-5304.5, -82724.617, 376.897), Now + 100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200);

        var hitanya = session.Targets!.Values.FirstOrDefault(n => n.NpcId == HithanyaNpcId);
        Assert.NotNull(hitanya);

        var lockOn = new PacketWriter().U16(0x3830).U8(0).Varint((uint)hitanya!.Key).ToArray();
        var replies = await session.HandleAsync(lockOn, Now + 300);

        Assert.True(replies.Any(p => Op(p) == 0xe241), "F talk via 0x3830 lock-on should open NPC dialog (0xe241) for open AskNpc step");
    }

    static (int QuestId, byte Status, int Step, int Uid, int PublisherId, int RollCount) ReadQuestBody(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var questId = r.I32();
        var status = r.U8();
        var step = (int)r.Varint();
        for (var i = 0; i < 6; i++) r.Varint();
        var uid = r.I32();
        var roll = (int)r.Varint();
        var publisher = r.I32();
        return (questId, status, step, uid, publisher, roll);
    }
}
