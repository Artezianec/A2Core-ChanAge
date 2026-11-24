using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// SkillAbnormalEffect rows for stigma self-buff abnormals (tools/stigmas.py abnormal_effects), only ids that class items and
/// skill-areas do not already own.
/// </summary>
public static class StigmaAbnormals
{
    sealed record File([property: JsonPropertyName("abnormal_effects")] Dictionary<string, AbnormalEffectEntry>? AbnormalEffects);

    sealed record AbnormalEffectEntry([property: JsonPropertyName("effects")] JsonElement[][] Effects, [property: JsonPropertyName("source")] string? Source);

    static readonly Lazy<File> file = new(Load);

    static File Load()
    {
        using var stream = typeof(StigmaAbnormals).Assembly.GetManifestResourceStream("stigmas.json")
            ?? throw new InvalidOperationException("Missing embedded game data stigmas.json");
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<File>(reader.ReadToEnd()) ?? new File(null);
    }

    public static AbnormalInfo? Info(int abnormalId) =>
        file.Value.AbnormalEffects?.GetValueOrDefault(abnormalId.ToString(CultureInfo.InvariantCulture)) is { } entry
            ? new AbnormalInfo(abnormalId, entry.Effects.Select(e => (e[0].GetInt32(), e[1].GetString()!,
                (IReadOnlyList<string>)e.Skip(2).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToList())).ToList())
            : null;
}