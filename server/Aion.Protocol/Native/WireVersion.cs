namespace Aion.Protocol.Native;

/// <summary>The client wire generation a native connection negotiated with its greeting.</summary>
public enum WireVersion
{
    V209 = 0,
    V212 = 1,
    V213 = 2,
}

/// <summary>
/// Per-connection wire version. The native listener sets it from the greeting before the first
/// encrypted frame; everything else reads it from the same async flow.
/// </summary>
public static class WireContext
{
    static readonly AsyncLocal<WireVersion> CurrentVersion = new();

    public static WireVersion Current
    {
        get => CurrentVersion.Value;
        set => CurrentVersion.Value = value;
    }

    public static bool IsModern => Current != WireVersion.V209;

    public static string Name(WireVersion version) => version switch
    {
        WireVersion.V212 => "2.0.12",
        WireVersion.V213 => "2.0.13",
        _ => "2.0.9",
    };
}
