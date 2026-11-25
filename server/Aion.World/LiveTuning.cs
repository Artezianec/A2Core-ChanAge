using System.Globalization;

namespace Aion.World;

/// <summary>
/// Live test knobs the host reads from <c>.start-all/runtime/dash-variant.txt</c>. The client's chat does not reach the
/// builder (0x362e) in this build, so a plain text file lets a live session change the sprint dash's reply shape without a
/// rebuild or a server restart. The value is applied on the next 0x3704 sprint request.
/// </summary>
public static class LiveTuning
{
    public const string DashVariantFile = "dash-variant.txt";

    static long stamp;
    static int cached = -1;

    /// <summary>Off by default so tests never pick up the machine's runtime file; the host enables it at startup.</summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// The variant written in the tuning file, or null when there is no file, it is not readable, or the value is out of
    /// range. The file's mtime caches the parse, so calling this per sprint is a single stat.
    /// </summary>
    public static int? DashVariant()
    {
        if (!Enabled) return null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".start-all", "runtime", DashVariantFile);
            if (File.Exists(path)) return Read(path);
        }
        return null;
    }

    /// <summary>The parsed variant for a file, cached by its last write time (exposed for tests).</summary>
    public static int? Read(string path)
    {
        try
        {
            var at = File.GetLastWriteTimeUtc(path).Ticks;
            if (at == Interlocked.Read(ref stamp) && cached >= 0) return cached;
            var text = File.ReadAllText(path).Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value is < 0 or > GameSession.DashVariantMax) return null;
            cached = value;
            Interlocked.Exchange(ref stamp, at);
            return value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
