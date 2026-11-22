using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Main hero quest codes per race (tools/data/hero-chains.json).</summary>
public sealed class HeroChains
{
    sealed record File([property: JsonPropertyName("light")] string[] Light, [property: JsonPropertyName("dark")] string[] Dark);

    static readonly Lazy<HeroChains> embedded = new(() =>
    {
        using var stream = typeof(HeroChains).Assembly.GetManifestResourceStream("hero-chains.json")
            ?? throw new InvalidOperationException("hero-chains.json is not embedded");
        var file = JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("hero-chains.json");
        return new HeroChains(file.Light, file.Dark);
    });

    public static HeroChains Embedded => embedded.Value;

    public HeroChains(string[] light, string[] dark) { Light = light; Dark = dark; }

    public string[] Light { get; }
    public string[] Dark { get; }

    public IReadOnlyList<string> ForRace(int race) => race == 2 ? Dark : Light;
}