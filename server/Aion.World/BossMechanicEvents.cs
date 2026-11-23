using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Raised when a scripted boss mechanic telegraph starts (ground markers hook; see GameSession.OnMechanicTelegraphStarted).</summary>
public sealed record BossMechanicTelegraphEventArgs(
    int BossNpcId,
    int BossKey,
    string MechanicId,
    int SkillId,
    Vector3d Centre,
    long StartedMs,
    long EndsMs,
    int[]? EnvObjIds = null,
    bool SpawnEnv = false,
    bool Despawn = false,
    int EnvSlot = 0);