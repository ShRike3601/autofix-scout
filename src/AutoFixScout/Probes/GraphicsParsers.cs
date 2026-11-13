using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoFixScout.Probes;

/// <summary>Pure text parsers for the graphics probe (unit tested from sample text).</summary>
public static class GraphicsParsers
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Field list of the nvidia-smi query. prefix is "clocks_throttle_reasons" or the newer "clocks_event_reasons".</summary>
    public static string NvidiaQuery(string prefix) =>
        "name,temperature.gpu,clocks.gr,clocks.max.gr,clocks.mem,clocks.max.mem,power.draw,power.limit," +
        "pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max," +
        $"{prefix}.active,{prefix}.hw_thermal_slowdown,{prefix}.sw_thermal_slowdown,{prefix}.hw_power_brake_slowdown";

    public const int NvidiaFields = 16;

    private static string? Clean(string s)
    {
        s = s.Trim();
        return s.Length == 0 || s.StartsWith('[') || s.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    private static int? I(string s) => Clean(s) is { } t && double.TryParse(t, NumberStyles.Float, Inv, out var d) ? (int)Math.Round(d) : null;
    private static double? D(string s) => Clean(s) is { } t && double.TryParse(t, NumberStyles.Float, Inv, out var d) ? d : null;
    private static bool? Active(string s) => Clean(s) is { } t ? t.Equals("Active", StringComparison.OrdinalIgnoreCase) : null;

    private static ulong? Mask(string s)
    {
        if (Clean(s) is not { } t) return null;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return ulong.TryParse(t, NumberStyles.HexNumber, Inv, out var v) ? v : null;
    }

    /// <summary>One reading per CSV row of "--format=csv,noheader,nounits". [N/A], [Not Supported] and empty fields become null. Rows with too few fields are skipped.</summary>
    public static IReadOnlyList<NvidiaReading> ParseNvidiaCsv(string? text)
    {
        var rows = new List<NvidiaReading>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < NvidiaFields) continue;
            // A name that contains a comma would shift every field: the extra parts belong to the name.
            var extra = parts.Length - NvidiaFields;
            var name = string.Join(",", parts.Take(extra + 1)).Trim();
            var f = parts.Skip(extra + 1).ToArray();
            rows.Add(new NvidiaReading(name, I(f[0]), I(f[1]), I(f[2]), I(f[3]), I(f[4]), D(f[5]), D(f[6]),
                I(f[7]), I(f[8]), I(f[9]), I(f[10]), Mask(f[11]), Active(f[12]), Active(f[13]), Active(f[14])));
        }
        return rows;
    }

    private static readonly Regex Bar1 = new(@"BAR1 Memory Usage\s*\r?\n\s*Total\s*:\s*(\d+)\s*MiB", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>BAR1 total (MiB) of the first GPU in "nvidia-smi -q -d MEMORY"; null when the section is missing or says N/A.</summary>
    public static long? ParseBar1TotalMiB(string? text) =>
        Bar1.Match(text ?? "") is { Success: true } m && long.TryParse(m.Groups[1].Value, out var v) ? v : null;

    /// <summary>Above 256 MiB the CPU can reach the whole video memory: Resizable BAR is on.</summary>
    public static Tri ResizableBarFromBar1(long? bar1TotalMiB) =>
        bar1TotalMiB is null ? Tri.Unknown : bar1TotalMiB > 256 ? Tri.Yes : Tri.No;

    private static readonly Regex Num = new(@"^-?\d+$");

    /// <summary>
    /// Parses a saved MSI Afterburner INI file. Keys read (case-insensitive): CoreClk / CoreClkBoost, MemClk / MemClkBoost
    /// (kHz offsets), CoreVoltage / CoreVoltageBoost (mV offset) and a non-empty VFCurve (custom curve). Offsets that are
    /// missing, empty or not numbers count as zero. When the "[Startup]" section holds an offset, the profile reports that
    /// section (it is what Afterburner applies when it starts); otherwise it reports the offsets found anywhere in the file.
    /// </summary>
    public static AfterburnerProfile ParseAfterburnerProfile(string file, string? text)
    {
        int core = 0, mem = 0, volt = 0, sCore = 0, sMem = 0, sVolt = 0;
        bool curve = false, sCurve = false;
        var section = "";
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = line[(eq + 1)..].Trim();
            var off = Num.IsMatch(val) && int.TryParse(val, NumberStyles.Integer, Inv, out var v) ? v : 0;
            var inStartup = section.Equals("Startup", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "coreclk" or "coreclkboost": if (off != 0) core = off; if (inStartup) sCore = off; break;
                case "memclk" or "memclkboost": if (off != 0) mem = off; if (inStartup) sMem = off; break;
                case "corevoltage" or "corevoltageboost": if (off != 0) volt = off; if (inStartup) sVolt = off; break;
                case "vfcurve":
                    if (VfCurveHasOffsets(val)) { curve = true; if (inStartup) sCurve = true; }
                    break;
            }
        }
        var startup = sCore != 0 || sMem != 0 || sVolt != 0 || sCurve;
        return startup ? new AfterburnerProfile(file, sCore, sMem, sVolt, sCurve, true)
                       : new AfterburnerProfile(file, core, mem, volt, curve, false);
    }

    /// <summary>
    /// Afterburner stores the NVIDIA voltage/frequency curve even when nothing is overclocked (checked on a real install,
    /// 2026-11-14): hex of an 8-byte header (format, point count), then per point three floats (offset, voltage mV,
    /// frequency MHz), then a block that is all zero at stock. A custom curve is any non-zero value other than the
    /// voltage and frequency of each point. Unreadable data counts as no custom curve.
    /// </summary>
    public static bool VfCurveHasOffsets(string? hex)
    {
        hex = hex?.Trim();
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0 || hex.Length < 16) return false;
        byte[] b;
        try { b = Convert.FromHexString(hex); } catch (FormatException) { return false; }
        var count = (long)BitConverter.ToUInt32(b, 4);
        var pointsEnd = 8 + count * 12;
        if (count == 0 || pointsEnd > b.Length) return false;
        for (var i = 0; i < count; i++)
            if (Math.Abs(BitConverter.ToSingle(b, 8 + i * 12)) > 0.5f) return true;
        for (var o = (int)pointsEnd; o + 4 <= b.Length; o += 4)
            if (Math.Abs(BitConverter.ToSingle(b, o)) > 0.5f) return true;
        return false;
    }

    /// <summary>"StartWithWindows=1" in the [Settings] section of Afterburner's own Profiles\MSIAfterburner.cfg.</summary>
    public static bool AfterburnerStartsWithWindows(string? settingsText) =>
        (settingsText ?? "").Split('\n').Any(l => l.Trim().Replace(" ", "").Equals("StartWithWindows=1", StringComparison.OrdinalIgnoreCase));

    /// <summary>Win32_SystemEnclosure chassis types of a desktop, tower, all-in-one or mini PC.</summary>
    public static readonly int[] DesktopChassis = { 3, 4, 5, 6, 7, 13, 15, 16, 23, 24, 34, 35, 36 };

    public static Tri IsDesktop(IEnumerable<int> chassisTypes, bool? hasBattery)
    {
        var types = chassisTypes.ToList();
        if (types.Count == 0) return Tri.Unknown;
        if (types.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32)) return Tri.No;
        if (!types.Any(DesktopChassis.Contains)) return Tri.Unknown;
        return hasBattery == true ? Tri.No : Tri.Yes;
    }
}
