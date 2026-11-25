using System.Collections.Concurrent;

namespace Aion.Protocol.Native;

/// <summary>
/// Per-connection flow journal: every native game packet and every login/select/world event in one readable file
/// (research/local/logs/flow-*.log). The file is resolved through an AsyncLocal so the listener, the session and the
/// send path all append to the same connection's journal without threading a logger through every call.
/// </summary>
public static class FlowLog
{
    static readonly AsyncLocal<string?> CurrentFile = new();
    static readonly ConcurrentDictionary<string, object> Gates = new();

    public static string? File => CurrentFile.Value;

    /// <summary>Points this async context at a fresh journal under the repository's research/local/logs folder.</summary>
    public static string Attach(string session)
    {
        var dir = LogDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"flow-{session}.log");
        CurrentFile.Value = path;
        lock (Gate(path)) System.IO.File.AppendAllText(path, $"# A2Core flow {session} started {DateTimeOffset.Now:O}{Environment.NewLine}");
        return path;
    }

    /// <summary>One packet. direction is C2S or S2C; handler is the code path or the opcode name.</summary>
    public static void Packet(string direction, ushort opcode, int size, string? handler = null, long? charId = null, int? mapId = null)
    {
        var line = $"{Stamp()} {direction} 0x{opcode:x4} {size,6} B  {handler ?? Name(opcode)}";
        if (charId is { } c) line += $" char={c}";
        if (mapId is { } m) line += $" map={m}";
        Append(line);
    }

    public static void Event(string name, string? extra = null) => Append($"{Stamp()} EVENT {name}{(string.IsNullOrEmpty(extra) ? "" : " " + extra)}");

    public static void Warn(string name, string? extra = null) => Append($"{Stamp()} WARN  {name}{(string.IsNullOrEmpty(extra) ? "" : " " + extra)}");

    public static void Error(string name, Exception? error = null) =>
        Append($"{Stamp()} ERROR {name}{(error is null ? "" : $": {error.GetType().Name}: {error.Message}")}");

    /// <summary>Writes a packet body as hex next to the journal (offline diff against an official capture frame).</summary>
    public static void Dump(string name, ReadOnlySpan<byte> body)
    {
        var path = CurrentFile.Value;
        if (path is null) return;
        try
        {
            var target = Path.ChangeExtension(path, null) + $"-{name}.hex";
            lock (Gate(path)) System.IO.File.WriteAllText(target, Convert.ToHexString(body));
        }
        catch (IOException) { }
    }

    static void Append(string line)
    {
        var path = CurrentFile.Value;
        if (path is null) return;
        try { lock (Gate(path)) System.IO.File.AppendAllText(path, line + Environment.NewLine); }
        catch (IOException) { }
    }

    static string Stamp() => DateTimeOffset.Now.ToString("HH:mm:ss.fff");

    static object Gate(string path) => Gates.GetOrAdd(path, _ => new object());

    static string LogDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var logs = Path.Combine(dir.FullName, "research", "local", "logs");
            if (Directory.Exists(logs)) return logs;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>Human names for the native opcodes the live flow uses (unknown ones fall back to the hex).</summary>
    public static string Name(ushort opcode) => opcode switch
    {
        0x16FE => "Handshake_RQ_v3",
        0x83A9 => "Handshake_RS_v3/SelectCharacter",
        0x8CFD => "NPLogin_RQ_v3",
        0x3615 => "Login_RS",
        0xC4F6 => "SelectCharacter_RQ_v3",
        0x2053 => "SelectCharacter_RS_v3",
        0x3621 => "PrepareEnterMap_NT",
        0x3622 => "LoadComplete_RQ",
        0xB24E => "LoadComplete_RQ_v2",
        0x29A7 => "LoadComplete_RQ_v3",
        0x4EF8 => "LoadComplete_RS",
        0x9651 => "LoadComplete_RS_v3",
        0x3633 => "MyCharAppear_NT",
        0x3634 => "EnvObjSpawn_NT",
        0x3641 => "NpcAppear_NT",
        0x382A => "AbnormalAdd_NT",
        0x364A => "CharStatsUpdate_NT",
        0x3656 => "CharCombatPowerUpdate_NT",
        0x8D00 => "CharPointUpdate_NT",
        0xE25A => "NCGuardData_RS",
        0x61A0 => "ClientSettings_NT",
        0x3600 => "MapFrame_NT",
        0x3601 => "Tick_RQ",
        0x3602 => "Ping_RQ",
        0x3603 => "Ping_RS",
        0xE259 => "NCGuardData_RQ",
        0xFFA0 => "GuardHeartbeat",
        0xFFA1 => "GuardHeartbeat2",
        0x3625 => "Lobby_RQ",
        0x3630 => "Logout_RQ",
        _ => "0x" + opcode.ToString("x4"),
    };
}
