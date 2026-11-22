namespace Aion.HeadlessClient.Protocol;

public sealed class SessionMetrics
{
    readonly object gate = new();
    readonly List<double> loginMs = [];
    readonly List<double> worldEntryMs = [];
    readonly List<double> pingMs = [];
    readonly List<UnparsedPacket> unparsed = [];
    readonly List<string> errors = [];
    int disconnects;

    public void RecordLogin(DateTimeOffset started) => Add(loginMs, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
    public void RecordWorldEntry(DateTimeOffset started) => Add(worldEntryMs, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
    public void RecordPing(DateTimeOffset sent, ulong echoedCounter) => Add(pingMs, (DateTimeOffset.UtcNow - sent).TotalMilliseconds);
    public void RecordDisconnect() { lock (gate) disconnects++; }
    public void RecordError(string message) { lock (gate) errors.Add(message); }

    public void RecordServerPacket(Aion.Protocol.Native.ParsedServerPacket packet)
    {
        if (packet.Parsed) return;
        lock (gate) unparsed.Add(new UnparsedPacket(packet.Opcode, packet.Name, packet.Body.Length));
    }

    public RunReport Report(string scenario, TimeSpan duration) => new()
    {
        Scenario = scenario,
        DurationSeconds = duration.TotalSeconds,
        LoginMs = Snapshot(loginMs),
        WorldEntryMs = Snapshot(worldEntryMs),
        PingMs = Snapshot(pingMs),
        Disconnects = disconnects,
        UnparsedPackets = [.. unparsed],
        Errors = [.. errors],
    };

    static void Add(List<double> bucket, double value) { lock (bucket) bucket.Add(value); }

    static LatencyStats Snapshot(List<double> values)
    {
        double[] copy;
        lock (values) copy = [.. values];
        if (copy.Length == 0) return new LatencyStats();
        Array.Sort(copy);
        return new LatencyStats
        {
            Count = copy.Length,
            P50 = Percentile(copy, 0.5),
            P95 = Percentile(copy, 0.95),
            Max = copy[^1],
        };
    }

    static double Percentile(double[] sorted, double p) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
}

public sealed record RunReport
{
    public required string Scenario { get; init; }
    public double DurationSeconds { get; init; }
    public LatencyStats LoginMs { get; init; } = new();
    public LatencyStats WorldEntryMs { get; init; } = new();
    public LatencyStats PingMs { get; init; } = new();
    public int Disconnects { get; init; }
    public IReadOnlyList<UnparsedPacket> UnparsedPackets { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record LatencyStats
{
    public int Count { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double Max { get; init; }
}

public sealed record UnparsedPacket(ushort Opcode, string Name, int Length);

public sealed class AggregateReport
{
  public List<RunReport> Accounts { get; } = [];
}