using System.Text.Json;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The engine, tools/data/boss-mechanics.schema.json and tools/boss_mechanics_validate.py name the same triggers, actions, shapes and anchors.</summary>
public sealed class EncounterVocabularyTests
{
    static JsonElement Defs()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "tools/data/boss-mechanics.schema.json"))) dir = Directory.GetParent(dir)?.FullName;
        Assert.NotNull(dir);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "tools/data/boss-mechanics.schema.json"))).RootElement.GetProperty("$defs");
    }

    static string[] Enum(string def) => Defs().GetProperty(def).GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public void Schema_and_engine_share_the_vocabulary()
    {
        Assert.Equal(EncounterVocabulary.Triggers.Order(), Enum("trigger_type").Order());
        Assert.Equal(EncounterVocabulary.Actions.Order(), Enum("action_type").Order());
        Assert.Equal(EncounterVocabulary.Shapes.Order(), Enum("shape_name").Order());
        Assert.Equal(EncounterAnchors.Keywords.Order(), Enum("anchor_keyword").Order());
    }

    [Fact]
    public void Every_difficulty_name_in_the_schema_maps_to_an_engine_difficulty()
    {
        foreach (var name in Enum("difficulty_name"))
            Assert.Contains(EncounterVocabulary.Difficulty(name), EncounterVocabulary.Difficulties);
    }

    [Fact]
    public void Every_action_field_in_the_schema_is_read_by_the_model()
    {
        var fields = Defs().GetProperty("action").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        var model = typeof(ActionDef).GetProperties().Where(p => p.Name != nameof(ActionDef.Compiled))
            .Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)).ToHashSet();
        Assert.Empty(fields.Except(model));
        Assert.Empty(model.Except(fields));
    }
}
