using Aion.Protocol.Native;

namespace Aion.HeadlessClient.Protocol;

public sealed class ScenarioFile
{
    public string Name { get; set; } = "unnamed";
    public string Mode { get; set; } = "development";
    public string? SharedSecret { get; set; }
    public List<ScenarioStep> Steps { get; set; } = [];
}

public sealed class ScenarioStep
{
    public string Action { get; set; } = "idle";
    public double Seconds { get; set; } = 5;
    public double Speed { get; set; } = 500;
    public double ReportRate { get; set; } = 4;
    public double MaxSeconds { get; set; } = 20;
    public List<float[]> Points { get; set; } = [];
    public uint? TargetKey { get; set; }
    public int? SkillId { get; set; }
    public int? NpcKey { get; set; }
    public string? Text { get; set; }
    public int? QuestId { get; set; }

    public IReadOnlyList<Vector3d> Route() =>
        Points.Select(p => p.Length >= 3 ? new Vector3d(p[0], p[1], p[2]) : Vector3d.Zero).ToList();
}