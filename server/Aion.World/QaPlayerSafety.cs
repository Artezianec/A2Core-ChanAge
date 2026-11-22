using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Production safeguards so QA bots do not interfere with real players.</summary>
public static class QaPlayerSafety
{
    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>A non-bot player on the same map/instance within <paramref name="rangeCm"/> of <paramref name="at"/>.</summary>
    public static bool RealPlayerNear(GameSession qa, Vector3d at, double rangeCm)
    {
        if (qa.WorldPlayer?.Map is not { } map) return false;
        foreach (var other in map.Players.Values)
        {
            if (other.Session == qa || other.Session.QaBot || other.Session.Bot) continue;
            if (other.Session.InstanceKey != qa.InstanceKey) continue;
            if (Flat(other.Position, at) <= rangeCm) return true;
        }
        return false;
    }

    public static bool ShouldDeferKill(GameSession qa, Vector3d targetAt, QaBotOptions options) =>
        options.PlayerProximityCm > 0 && RealPlayerNear(qa, targetAt, options.PlayerProximityCm);

    /// <summary>The mob is fighting a real player (not another bot).</summary>
    public static bool RealPlayerEngagedWith(GameSession qa, Target npc)
    {
        if (qa.SharedMap?.Ai is not { } ai || !ai.Brains.TryGetValue(npc.Key, out var brain) || !brain.Engaged || brain.VictimIsNpc)
            return false;
        if (qa.WorldPlayer?.Map is not { } map) return false;
        return map.Players.GetValueOrDefault(brain.VictimKey) is { } victim
            && !victim.Session.QaBot && !victim.Session.Bot;
    }

    public static bool MustNotAttack(GameSession qa, Target npc, QaBotOptions options) =>
        ShouldDeferKill(qa, npc.Position, options) || RealPlayerEngagedWith(qa, npc);
}