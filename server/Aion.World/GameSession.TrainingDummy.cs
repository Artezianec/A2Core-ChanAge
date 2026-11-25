namespace Aion.World;

partial class GameSession
{
    /// <summary>Open KillNpc on quests or map events: scripted kills override the retail invuln rule.</summary>
    bool TrainingDummyKillAllowed(Target target) =>
        !Combat.IsTrainingDummy(target)
        || questLog?.OpenKillNpcIds().Contains(target.NpcId) == true
        || mapEventRuns.Any(r => r.OpenKillNpcIds().Contains(target.NpcId));

    int ApplyDamageToNpc(Target target, int damage) =>
        Combat.HpAfterDamage(target, target.Hp, damage, TrainingDummyKillAllowed(target));

    internal int ApplyDamageToNpcForTests(Target target, int damage) => ApplyDamageToNpc(target, damage);
    internal bool TrainingDummyKillAllowedForTests(Target target) => TrainingDummyKillAllowed(target);
}
