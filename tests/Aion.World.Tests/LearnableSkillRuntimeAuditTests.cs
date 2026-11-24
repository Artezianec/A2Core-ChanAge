using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LearnableSkillRuntimeAuditTests
{
    const long Now = 1_761_000_100_000;

    static async Task<GameSession> ClassSessionAsync(string className)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var creation = Enumerable.Range(1, 64).First(c =>
            data.Profile(c, 1) is { } p && string.Equals(p.Class, className, StringComparison.OrdinalIgnoreCase));
        var store = new FakeCharacterStore { Progress = (45, 0) };
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(className.GetHashCode())) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Rt").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        foreach (var (id, _) in data.LearnedSkills(className, data.MaxLevel))
            session.LearnForTests(id);
        session.SetResource('M', session.Resource!.MpMax);
        var mob = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(mob.Position with { X = mob.Position.X + 400 }), Now + 1_000);
        return session;
    }

    public static TheoryData<string, int> RepresentativeSkills => new()
    {
        { "Assassin", 13_060_000 },
        { "Assassin", 13_210_000 },
        { "Cleric", 17_060_000 },
        { "Sorcerer", 15_310_000 },
    };

    [Theory]
    [MemberData(nameof(RepresentativeSkills))]
    public async Task Representative_skills_cast_and_damage(string className, int skillId)
    {
        var session = await ClassSessionAsync(className);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var row = await LearnableSkillCastAudit.ProbeAsync(session, GameSessionTestHelpers.LiveGameData, className, skillId, Now + 2_000, mob);
        Assert.Equal(LearnableSkillCastAudit.RuntimeStatus.Ok, row.Status);
    }

    [Fact]
    public async Task Write_runtime_failure_report()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var failures = new List<string>();
        foreach (var className in LearnableSkillAudit.PlayableClasses)
        {
            var session = await ClassSessionAsync(className);
            var mob = session.Targets![Combat.TrainingDummyKey];
            var profile = data.ProfileForClass(1, 1, className)!;
            var skills = data.SkillListForClient(className, data.MaxLevel, profile)
                .Select(k => k.SkillId).Where(id => id % 10_000 == 0 && data.Skill(id) is { Type: "Active" });
            var t = Now + 4_000;
            foreach (var skillId in skills)
            {
                session.SetResource('M', session.Resource!.MpMax);
                var row = await LearnableSkillCastAudit.ProbeAsync(session, data, className, skillId, t, mob);
                t += 20_000;
                if (row.Status == LearnableSkillCastAudit.RuntimeStatus.Fail)
                    failures.Add($"{className}|{row.SkillId}|{row.Name}|{row.Reason}");
            }
        }
        var path = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "docs", "skills-audit-runtime.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = "# Runtime cast probe failures\n\n"
            + $"Skills with `Fail` from `LearnableSkillCastAudit` (mastery roots, level 45). "
            + $"Regenerate: `dotnet test --filter LearnableSkillRuntimeAuditTests.Write_runtime_failure_report`.\n\n"
            + $"Total: **{failures.Count}**\n\n| Class | Id | Name | Reason |\n| --- | ---: | --- | --- |\n"
            + string.Join('\n', failures.Select(l =>
            {
                var p = l.Split('|');
                return $"| {p[0]} | {p[1]} | {p[2]} | {p[3]} |";
            }));
        File.WriteAllText(path, body);
    }

}