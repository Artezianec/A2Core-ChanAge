using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Map-event id on 0x8d2c during ascension subspace ring flight (capture 20261101 flight quest).</summary>
    public const int AscensionFlightMapEventWireId = 1415996;
    static readonly int[] AscensionFlightRingSteps = [4, 5, 3, 6, 4, 7];
    const int StatFlightTimeWire = 214; // 0xd6
    const int StatFlyCostWire = 247;    // 0xf7
    const int FlightRingRefillFlightTime = 15_000;
    const int FlightRingRefillFlyCost = 10_000;
    /// <summary>SkillEffectByAbnormal row on ring pass (capture 0x3805 … 0x532e0b46).</summary>
    const int FlightRingPassEffectId = unchecked((int)0x532E0B46);

    int ascensionFlightRingPasses;
    long? flightRingStatResetAtMs;

    bool AscensionFlightTutorialActive => worldMap == 200005 && instanceKey != 0;

    /// <summary>Trigger volume reported as env interact: credit EnterVolumePC and flush map-event directives (EventFunc + interact chain same batch).</summary>
    List<byte[]> MapEventTriggerInteract(string triggerName, long now)
    {
        if (mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        bool Inside(string name) => string.Equals(name, triggerName, StringComparison.OrdinalIgnoreCase);
        foreach (var run in mapEventRuns.ToList()) run.PositionAt(Inside, _ => false, now);
        return DrainMapEvents();
    }

    List<byte[]> FlightRingPassPackets(int ringKey, long now)
    {
        var replies = new List<byte[]> { GamePackets.InteractEnvObjReply(ringKey) };
        var at = position ?? Vector3d.Zero;
        if (resources is { FpMax: > 0 } r)
        {
            r.Fp = r.FpMax;
            replies.Add(GamePackets.CharStatsUpdate(playerKey,
                [(StatFlightTimeWire, FlightRingRefillFlightTime), (StatFlyCostWire, FlightRingRefillFlyCost)], r.HpMax));
            flightRingStatResetAtMs = now + 3000;
            replies.Add(PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, FlightRingAbnormal, 3000, now + 3000, CasterKey: playerKey, RangeBase: at)]));
            replies.Add(RidePackets.AbnormalEffect(playerKey, playerKey, buffUid, FlightRingPassEffectId, FlightRingRefillFlyCost, 0, 0));
            replies.AddRange(FpWireUpdates());
        }
        if (AscensionFlightTutorialActive)
        {
            var step = AscensionFlightRingSteps[Math.Min(ascensionFlightRingPasses, AscensionFlightRingSteps.Length - 1)];
            ascensionFlightRingPasses++;
            replies.Add(GamePackets.QuestStepTalkReply(2, AscensionFlightMapEventWireId, 0, step));
        }
        return replies;
    }

    List<byte[]> FlightRingStatResetTick(long now)
    {
        if (flightRingStatResetAtMs is not { } due || now < due || resources is null) return [];
        flightRingStatResetAtMs = null;
        return
        [
            GamePackets.CharStatsUpdate(playerKey, [(StatFlightTimeWire, 0), (StatFlyCostWire, 0)], resources.HpMax),
        ];
    }

    /// <summary>After ascension flight, exit Dream Crossroad to Ishalgen / Poeta field (capture reason 0x0c on 0x3647).</summary>
    public const byte DreamCrossroadFieldExitReason = 12;

    static Vector3d DreamCrossroadFieldExitAt(int race) =>
        race == 2
            ? new Vector3d(21550, -23574, 5058)
            : MapEventTables.Embedded.Teleport("TeleportArtifact_Ishalgen_Region_03_2") is { At: [var x, var y, var z] }
                ? new Vector3d(x, y, z)
                : new Vector3d(21274, -23850, 5064.9);
}
