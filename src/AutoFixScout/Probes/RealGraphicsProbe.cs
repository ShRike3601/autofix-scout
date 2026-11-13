using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.graphics: video controllers (Win32_VideoController), NVIDIA readings (nvidia-smi.exe, only when it exists),
/// Afterburner / overclocking tools (registry, folders, running processes, saved profile files) and driver resets (the
/// health check's reader). Read-only, no admin, every part separate; a part that fails is named in ProbeErrors.
/// </summary>
public sealed class RealGraphicsProbe : IGraphicsProbe
{
    public GraphicsState Read()
    {
        var s = new GraphicsState();
        var errors = new List<string>();
        void Part(string name, Action read)
        {
            try { read(); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }
        Part("video controllers", () => s.Gpus = ReadGpus());
        Part("chassis", () => s.IsDesktop = ReadIsDesktop());
        Part("nvidia-smi", () => ReadNvidia(s));
        Part("overclocking tools", () => s.Tools = ReadTools());
        Part("graphics resets", () => s.GraphicsResets = RealHealthProbe.CountGraphicsResets());
        s.ProbeErrors = errors;
        return s;
    }

    private static List<GpuInfo> ReadGpus()
    {
        using var q = new ManagementObjectSearcher(
            "SELECT Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, PNPDeviceID FROM Win32_VideoController");
        var list = new List<GpuInfo>();
        foreach (ManagementBaseObject o in q.Get())
            using (o)
            {
                var name = o["Name"] as string ?? "";
                DateTime? date = null;
                if (o["DriverDate"] is string d && d.Length >= 8)
                    try { date = ManagementDateTimeConverter.ToDateTime(d).ToUniversalTime(); } catch (Exception) { }
                list.Add(new GpuInfo(name, o["DriverVersion"] as string, date,
                    o["AdapterRAM"] is uint ram ? ram : null, GpuInfo.Classify(name),
                    o["CurrentHorizontalResolution"] is uint w && w > 0, o["PNPDeviceID"] as string));
            }
        return list;
    }

    private static Tri ReadIsDesktop()
    {
        var chassis = new List<int>();
        using (var q = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            foreach (ManagementBaseObject o in q.Get())
                using (o)
                    if (o["ChassisTypes"] is ushort[] t) chassis.AddRange(t.Select(x => (int)x));
        bool? battery = null;
        try
        {
            using var b = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            battery = b.Get().Count > 0;
        }
        catch (Exception) { }
        return GraphicsParsers.IsDesktop(chassis, battery);
    }

    // ---- NVIDIA ----

    private static string? NvidiaSmiPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static (int Exit, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(true); } catch (Exception) { }
            return (-1, "");
        }
        return (p.ExitCode, outTask.GetAwaiter().GetResult());
    }

    /// <summary>
    /// One nvidia-smi query (about 100 to 300 ms). The throttle fields were renamed from clocks_throttle_reasons to
    /// clocks_event_reasons; older drivers know only the first. Pass the prefix a previous call returned to skip the retry.
    /// Shared with the stress test.
    /// </summary>
    internal static (IReadOnlyList<NvidiaReading> Rows, string? Prefix) QueryNvidia(string exe, string? knownPrefix = null)
    {
        foreach (var prefix in knownPrefix != null ? new[] { knownPrefix } : new[] { "clocks_throttle_reasons", "clocks_event_reasons" })
        {
            var (exit, text) = Run(exe, $"--query-gpu={GraphicsParsers.NvidiaQuery(prefix)}", "--format=csv,noheader,nounits");
            var rows = exit == 0 ? GraphicsParsers.ParseNvidiaCsv(text) : new List<NvidiaReading>();
            if (rows.Count > 0) return (rows, prefix);
        }
        return (new List<NvidiaReading>(), null);
    }

    internal static string? FindNvidiaSmi() => NvidiaSmiPath();

    private static void ReadNvidia(GraphicsState s)
    {
        var exe = NvidiaSmiPath();
        s.NvidiaToolPresent = exe != null;
        if (exe == null) return;
        s.Nvidia = QueryNvidia(exe).Rows;
        var (e2, mem) = Run(exe, "-q", "-d", "MEMORY");
        if (e2 == 0)
        {
            s.Bar1TotalMiB = GraphicsParsers.ParseBar1TotalMiB(mem);
            s.ResizableBar = GraphicsParsers.ResizableBarFromBar1(s.Bar1TotalMiB);
        }
    }

    // ---- Overclocking tools ----

    private static readonly string[] UninstallKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    private static IEnumerable<(string Name, string? Location)> InstalledPrograms()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var path in UninstallKeys)
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n) yield return (n, k.GetValue("InstallLocation") as string);
                }
            }
    }

    private static OverclockToolState ReadTools()
    {
        var t = new OverclockToolState();
        var programs = InstalledPrograms().ToList();
        var abDir = programs.FirstOrDefault(p => p.Name.Contains("Afterburner", StringComparison.OrdinalIgnoreCase)).Location;
        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner");
        var dir = !string.IsNullOrWhiteSpace(abDir) && Directory.Exists(abDir) ? abDir : defaultDir;
        var installed = programs.Any(p => p.Name.Contains("Afterburner", StringComparison.OrdinalIgnoreCase)) || Directory.Exists(defaultDir);
        t.AfterburnerInstalled = installed ? Tri.Yes : Tri.No;
        var other = new List<string>();
        foreach (var p in programs)
            if (p.Name.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("EVGA Precision", StringComparison.OrdinalIgnoreCase))
                other.Add(p.Name);
        t.OtherTools = other.Distinct().ToList();
        if (!installed) return t;

        t.AfterburnerRunning = Process.GetProcessesByName("MSIAfterburner").Length > 0 ? Tri.Yes : Tri.No;
        var profileDir = Path.Combine(dir, "Profiles");
        string? settings = null;
        try { settings = File.ReadAllText(Path.Combine(profileDir, "MSIAfterburner.cfg")); } catch (Exception) { /* unknown */ }
        t.AfterburnerStartsWithWindows = StartsWithWindows() || GraphicsParsers.AfterburnerStartsWithWindows(settings) ? Tri.Yes : Tri.No;
        var profiles = new List<AfterburnerProfile>();
        if (Directory.Exists(profileDir))
            foreach (var f in Directory.EnumerateFiles(profileDir, "*.cfg"))
                try { profiles.Add(GraphicsParsers.ParseAfterburnerProfile(Path.GetFileName(f), File.ReadAllText(f))); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        t.Profiles = profiles;
        return t;
    }

    /// <summary>A Run entry or a scheduled task that starts Afterburner when the person signs in.</summary>
    private static bool StartsWithWindows()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
            {
                using var k = hive.OpenSubKey(path);
                if (k == null) continue;
                foreach (var name in k.GetValueNames())
                    if (name.Contains("Afterburner", StringComparison.OrdinalIgnoreCase)
                        || (k.GetValue(name) as string)?.Contains("MSIAfterburner", StringComparison.OrdinalIgnoreCase) == true)
                        return true;
            }
        try
        {
            var tasks = Path.Combine(Environment.SystemDirectory, "Tasks");
            if (Directory.Exists(tasks) && Directory.EnumerateFiles(tasks, "*Afterburner*", SearchOption.AllDirectories).Any()) return true;
        }
        catch (Exception) { }
        return false;
    }
}
