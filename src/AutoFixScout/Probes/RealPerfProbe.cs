using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using AutoFixScout.Areas;
using AutoFixScout.Triage;
using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.perf: processor speed limit (performance counters sampled for a few seconds, Kernel-Processor-Power event 37),
/// power plan and battery, BIOS date, processor model and microcode, memory modules, and the blue screen / hardware
/// error / unexpected shutdown counts shared with the health check. Read-only, no admin. Every part is read
/// separately; a part that fails is named in ProbeErrors and the others still count.
/// </summary>
public sealed class RealPerfProbe : IPerfProbe
{
    public static readonly TimeSpan SampleTime = TimeSpan.FromSeconds(3);

    public PerfState Read()
    {
        var s = new PerfState();
        var errors = new List<string>();
        void Part(string name, Action read)
        {
            try { read(); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }

        Activity.Report(ActivityKind.Analyze, "Reading processor speed, power settings, memory and stability signals");
        Part("processor speed counters", () => SampleCounters(s));
        Part("processor speed events", () => s.FirmwareLimitEvents = CountFirmwareLimitEvents());
        Part("processor", () => ReadCpu(s));
        Part("microcode", () => s.MicrocodeRevision = ReadMicrocode());
        Part("BIOS", () => s.BiosDateUtc = ReadBiosDate());
        Part("power plan", () => s.ActiveSchemeGuid = PowerTriage.ParseActiveScheme(PowerFlow.Powercfg("/getactivescheme")));
        Part("power mode", () => s.OverlayGuid = ReadOverlay());
        Part("battery", () => ReadBattery(s));
        Part("memory", () => { s.Ram = ReadRam(); s.RamRead = true; });
        Part("hardware errors", () =>
        {
            var (blue, shut, whea) = RealHealthProbe.ReadInstabilitySignals();
            s.BlueScreens = blue; s.UnexpectedShutdowns = shut; s.HardwareErrors = whea; s.InstabilityRead = true;
        });
        s.ProbeErrors = errors;
        Activity.Report(ActivityKind.Analyze, errors.Count == 0 ? "Read all parts of the performance check" : $"{Activity.Count(errors.Count, "part")} of the performance check could not be read");
        return s;
    }

    // ---- performance counters (PDH; no extra package) ----

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] internal static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);
    [DllImport("pdh.dll")] internal static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)] internal struct PdhValue { public uint Status; public double Double; }
    internal const uint PdhFmtDouble = 0x200;

    private static void SampleCounters(PerfState s)
    {
        Activity.Report(ActivityKind.Load, $"Sampling processor counters for {Activity.Seconds(SampleTime)} (% Performance Limit, % Processor Performance)");
        if (PdhOpenQuery(null, IntPtr.Zero, out var q) != 0) throw new InvalidOperationException("counters not available");
        try
        {
            IntPtr limit = IntPtr.Zero, perf = IntPtr.Zero;
            var haveLimit = PdhAddEnglishCounterW(q, @"\Processor Information(_Total)\% Performance Limit", IntPtr.Zero, out limit) == 0;
            var havePerf = PdhAddEnglishCounterW(q, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out perf) == 0;
            if (!haveLimit && !havePerf) throw new InvalidOperationException("processor counters not available");
            PdhCollectQueryData(q);
            Thread.Sleep(SampleTime);
            if (PdhCollectQueryData(q) != 0) throw new InvalidOperationException("counters could not be sampled");
            if (haveLimit && PdhGetFormattedCounterValue(limit, PdhFmtDouble, out _, out var l) == 0 && l.Status == 0) s.PerformanceLimitPercent = Math.Round(l.Double, 1);
            if (havePerf && PdhGetFormattedCounterValue(perf, PdhFmtDouble, out _, out var p) == 0 && p.Status == 0) s.ProcessorPerformancePercent = Math.Round(p.Double, 1);
        }
        finally { PdhCloseQuery(q); }
    }

    private static int CountFirmwareLimitEvents()
    {
        const long week = 7L * 24 * 3600 * 1000;
        using var r = EventLogs.Open("System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=37 and TimeCreated[timediff(@SystemTime) <= {week}]]]", TimeSpan.FromDays(7));
        var n = 0;
        while (n < 5000 && r.ReadEvent() is { } e) { using (e) n++; }
        Activity.Report(ActivityKind.EventLog, $"Found {(n == 0 ? "no events" : Activity.Count(n, "event"))}: Kernel-Processor-Power 37, last 7 days");
        return n;
    }

    // ---- processor, BIOS, microcode ----

    private static void ReadCpu(PerfState s)
    {
        using var q = Wmi.Searcher("SELECT Name FROM Win32_Processor");
        s.CpuName = q.Get().Cast<ManagementBaseObject>().Select(m => m["Name"] as string).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();
    }

    private static uint? ReadMicrocode()
    {
        Activity.ReadingRegistry("HKLM", @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "(microcode revision)");
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return PerfTriage.ParseMicrocode(k?.GetValue("Update Revision") as byte[]);
    }

    private static DateTime? ReadBiosDate()
    {
        using var q = Wmi.Searcher("SELECT ReleaseDate FROM Win32_BIOS");
        foreach (ManagementBaseObject m in q.Get())
            if (m["ReleaseDate"] is string d && d.Length >= 14)
                return ManagementDateTimeConverter.ToDateTime(d).ToUniversalTime();
        return null;
    }

    // ---- power ----

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

    private static string? ReadOverlay()
    {
        Activity.Report(ActivityKind.Command, "Calling PowerGetEffectiveOverlayScheme (power mode)");
        return PowerGetEffectiveOverlayScheme(out var g) == 0 ? g.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatusFlag; public int LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>True on battery, false on mains, null when unknown. Shared with the stress test.</summary>
    internal static (bool? OnBattery, bool HasBattery) ReadPower() =>
        GetSystemPowerStatus(out var p)
            ? (p.AcLine switch { 0 => true, 1 => false, _ => null }, p.BatteryFlag is not (128 or 255))
            : (null, true);

    private static void ReadBattery(PerfState s)
    {
        Activity.Report(ActivityKind.Command, "Calling GetSystemPowerStatus (battery and Battery Saver)");
        if (!GetSystemPowerStatus(out var p)) throw new InvalidOperationException("power status not available");
        s.HasBattery
 = p.BatteryFlag is not (128 or 255);
        s.OnBattery = p.AcLine switch { 0 => true, 1 => false, _ => null };
        s.BatterySaverOn = p.SystemStatusFlag == 1;
    }

    // ---- memory ----

    private static IReadOnlyList<RamModule> ReadRam()
    {
        using var q = Wmi.Searcher("SELECT Capacity, Speed, ConfiguredClockSpeed, FormFactor, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        return q.Get().Cast<ManagementBaseObject>().Select(m => new RamModule(
            m["Capacity"] is { } c ? Convert.ToInt64(c) : 0,
            Speed(m["Speed"]), Speed(m["ConfiguredClockSpeed"]),
            m["FormFactor"] is { } f ? Convert.ToInt32(f) : null,
            m["SMBIOSMemoryType"] is { } t ? Convert.ToInt32(t) : null)).ToList();
    }

    private static int? Speed(object? v) => v is null ? null : Convert.ToInt32(v) is var n and > 0 ? n : null;
}
