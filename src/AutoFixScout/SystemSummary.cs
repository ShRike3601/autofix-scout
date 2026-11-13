using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AutoFixScout;

/// <summary>One line of the "This computer" panel: a label and a value, both safe to show and to copy for a helper.</summary>
public sealed record SummaryLine(string Label, string Value, string Short);

/// <summary>
/// The "This computer" facts for the sidebar. Read-only and quick (registry, two small WMI queries, Win32 calls).
/// No personal data: no user name, computer name, serial numbers or network names.
/// </summary>
public static class SystemSummary
{
    public static IReadOnlyList<SummaryLine> Read()
    {
        var lines = new List<SummaryLine>();
        void Add(string label, Func<string?> read)
        {
            try { if (read() is { Length: > 0 } v) lines.Add(new SummaryLine(label, v, Shorten(label, v))); }
            catch (Exception) { /* a missing fact is left out */ }
        }
        Add("Windows", WindowsVersion);
        Add("Processor", () => Clean(Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string));
        Add("Graphics", Graphics);
        Add("Memory", () => Memory() is { } gb ? $"{gb:0} GB" : null);
        Add("Disk C:", () => DiskText(new DriveInfo("C")));
        Add("Battery", Battery);
        Add("Running for", () => Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        return lines;
    }

    /// <summary>The one-line form for the sidebar ("Core i7-10750H", "420 GB free"); the full value goes in the tooltip and the copy.</summary>
    public static string Shorten(string label, string value)
    {
        string Drop(string s, params string[] words) { foreach (var w in words) s = s.Replace(w, "", StringComparison.OrdinalIgnoreCase); return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)); }
        switch (label)
        {
            case "Windows":
                var paren = value.IndexOf(" (build", StringComparison.Ordinal);
                var w = paren > 0 ? value[..paren] : value;
                return Drop(w, "Single Language", "Education", "Workstations").Replace("Windows 11 Home N", "Windows 11 Home");
            case "Processor":
                return System.Text.RegularExpressions.Regex.Replace(Drop(value, "Intel ", "AMD ", " Processor"), @" \d+-Core", "").Trim();
            case "Graphics":
                var first = value.Split(" + ")[0];
                return Drop(first, "NVIDIA ", "AMD ", "(R)", "Graphics Family");
            case "Memory": return value + " memory";
            case "Disk C:":
                var of = value.IndexOf(" of ", StringComparison.Ordinal);
                return (of > 0 ? value[..of] : value) + " on C:";
            case "Running for": return "On for " + value;
            default: return value;
        }
    }

    /// <summary>"Windows 11 Home 24H2 (build 26100)". Windows 11 still reports "Windows 10" in ProductName; build 22000+ is 11.</summary>
    public static string? WindowsVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (k is null) return null;
        var build = int.TryParse(k.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        return FormatWindows(k.GetValue("ProductName") as string, k.GetValue("DisplayVersion") as string, build);
    }

    public static string FormatWindows(string? product, string? displayVersion, int build)
    {
        var name = (product ?? "Windows").Trim();
        if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return $"{name}{(string.IsNullOrWhiteSpace(displayVersion) ? "" : " " + displayVersion.Trim())} (build {build})";
    }

    /// <summary>Removes the vendor noise from CPU and GPU names: "(R)", "(TM)", "CPU @ 2.60GHz", repeated spaces.</summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace(" CPU", "");
        var at = s.IndexOf(" @ ", StringComparison.Ordinal);
        if (at > 0) s = s[..at];
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The dedicated graphics card when there is one, otherwise the first adapter; several are joined with " + ".</summary>
    private static string? Graphics()
    {
        using var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
        var names = q.Get().Cast<ManagementObject>().Select(o => Clean(o["Name"] as string)).OfType<string>()
            .Where(n => !n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && !n.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        return GraphicsText(names);
    }

    public static string? GraphicsText(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return null;
        static bool Dedicated(string n) => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                                           || n.Contains("Arc A", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc B", StringComparison.OrdinalIgnoreCase);
        return string.Join(" + ", names.OrderByDescending(Dedicated));
    }

    private static double? Memory()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? Math.Round(m.TotalPhys / 1024d / 1024 / 1024) : null;
    }

    private static string? DiskText(DriveInfo d) => d.IsReady ? DiskText(d.AvailableFreeSpace, d.TotalSize) : null;

    public static string DiskText(long free, long total) =>
        $"{free / 1e9:0} GB free of {total / 1e9:0} GB";

    private static string? Battery()
    {
        if (!GetSystemPowerStatus(out var p) || p.BatteryFlag == 128 || p.BatteryFlag == 255) return null; // no battery / unknown
        return BatteryText(p.BatteryLifePercent, p.ACLineStatus == 1);
    }

    public static string? BatteryText(byte percent, bool pluggedIn) =>
        percent > 100 ? null : $"{percent} %{(pluggedIn ? ", plugged in" : ", on battery")}";

    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";

    /// <summary>All lines as plain text, for "copy for your helper".</summary>
    public static string AsText(IEnumerable<SummaryLine> lines) => string.Join(Environment.NewLine, lines.Select(l => $"{l.Label}: {l.Value}"));

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
