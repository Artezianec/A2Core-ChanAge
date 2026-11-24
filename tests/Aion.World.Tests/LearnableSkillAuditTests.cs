using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Theory audit of every learnable class skill at progression cap (plus specialization children).</summary>
public sealed class LearnableSkillAuditTests
{
    static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Every_learnable_skill_passes_data_audit_or_is_candidate()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var rows = LearnableSkillAudit.Run(data);
        var failures = rows.Where(r => r.Status == LearnableSkillAudit.Status.Fail).ToList();
        Assert.True(failures.Count == 0,
            string.Join(Environment.NewLine, failures.Take(40).Select(f => $"{f.Class} {f.SkillId} {f.Name}: {f.Reason}"))
            + (failures.Count > 40 ? $"{Environment.NewLine}… and {failures.Count - 40} more" : ""));
    }

    [Fact]
    public void Write_report()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var rows = LearnableSkillAudit.Run(data);
        var md = LearnableSkillAudit.FormatMarkdown(rows, data, data.MaxLevel);
        var outPath = Path.Combine(RepoRoot, "docs", "skills-audit.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var existing = File.Exists(outPath) ? File.ReadAllText(outPath) : null;
        if (existing != md)
            File.WriteAllText(outPath, md);
    }

    [Fact]
    public void Specialization_child_side_tables_match_parent_when_present()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var rows = LearnableSkillAudit.Run(data);
        var side = rows.Where(r => r.Reason.Contains("lookup", StringComparison.Ordinal) && r.Status == LearnableSkillAudit.Status.Fail).ToList();
        Assert.Empty(side);
    }
}