using System.Reflection;
using System.Text.Json;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ItemUseEffectCoverageTests
{
    [Fact]
    public async Task Every_distinct_use_effect_is_handled_not_direct_use_or_known_unimplemented()
    {
        using var stream = typeof(RewardBoxes).Assembly.GetManifestResourceStream("boxes.json")
            ?? throw new InvalidOperationException("embedded boxes.json missing");
        using var doc = await JsonDocument.ParseAsync(stream);
        var effects = doc.RootElement.GetProperty("uses").EnumerateObject()
            .Select(p => p.Value[0].GetString()).Where(s => s is not null).Select(s => s!).ToHashSet();
        var uncovered = effects.Where(e =>
            !ItemUseEffects.Handled.Contains(e) && !ItemUseEffects.NotDirectUse.Contains(e) && !ItemUseEffects.KnownUnimplemented.Contains(e)).Order().ToList();
        Assert.True(uncovered.Count == 0, "Uncovered UseEffect: " + string.Join(", ", uncovered));
    }

    [Fact]
    public void ItemSkillSupported_rejects_mixed_instant_and_unsupported_rows()
    {
        var method = typeof(GameSession).GetMethod("ItemSkillSupported", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var rows = new List<SkillEffectRow>
        {
            new("HpHeal_Item", ["5000"]),
            new("Teleport", ["0"]),
        };
        var supported = (bool)method!.Invoke(null, [rows])!;
        Assert.False(supported);
    }

    [Fact]
    public void ItemSkillSupported_accepts_mixed_instant_and_abnormal_rows()
    {
        const int speedBuff = 531060015;
        var skillId = RewardBoxes.Embedded.Use(speedBuff)?.Values is [var id, ..] && int.TryParse(id, out var parsed) ? parsed : 0;
        Assert.True(skillId > 0);
        var abnormalId = RewardBoxes.Embedded.SkillEffects(skillId).FirstOrDefault(e => e.Type == "Abnormal_True")?.Value(2) ?? 0;
        Assert.True(abnormalId > 0);

        var method = typeof(GameSession).GetMethod("ItemSkillSupported", BindingFlags.Static | BindingFlags.NonPublic);
        var rows = new List<SkillEffectRow>
        {
            new("HpHeal_Item", ["1"]),
            new("Abnormal_True", ["0", "0", abnormalId.ToString()]),
        };
        var supported = (bool)method!.Invoke(null, [rows])!;
        Assert.True(supported);
    }

}