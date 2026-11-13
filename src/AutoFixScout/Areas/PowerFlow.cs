using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using AutoFixScout.Triage;
using Microsoft.Win32;

namespace AutoFixScout.Areas;

/// <summary>"Battery, sleep and power". Real test: the person's answer. Everything read here works without admin rights.</summary>
public static class PowerFlow
{
    public const string Topic = "power";
    public const string What = "Battery, sleep and power";
    public const string TestQuestion = "Do the battery and sleep now work the way you expect?";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        var s = Read();
        var f = PowerTriage.Evaluate(s);
        return new Diagnosis(s, f, PowerTriage.Ladder(s, f), false, PowerTriage.NotFixedHint(f));
    });

    public static PowerState Read()
    {
        var s = new PowerState();
        var errors = new List<string>();
        void Part(string name, Action a) { try { a(); } catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); } }
        Part("battery", () => ReadBattery(s));
        Part("power plan", () => s.ActiveSchemeGuid = PowerTriage.ParseActiveScheme(Powercfg("/getactivescheme")));
        Part("sleep timeouts", () =>
        {
            if (PowerTriage.ParseAcDc(Powercfg("/query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE")) is { } t) { s.SleepAcSeconds = t.Ac; s.SleepDcSeconds = t.Dc; }
        });
        Part("hibernate", () => s.HibernateEnabled = ReadDword(@"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is { } v ? v != 0 : null);
        Part("fast startup", () => s.FastStartup = ReadDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled") is { } v ? v != 0 : null);
        Part("wake events", () => ReadEvents(s));
        s.ProbeErrors = errors;
        return s;
    }

    private static int? ReadDword(string key, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(name) is int i ? i : null;
    }

    internal static string Powercfg(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("powercfg timed out"); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"powercfg {args} exit {p.ExitCode}");
        return o.Result;
    }

    private static void ReadBattery(PowerState s)
    {
        using (var b = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining FROM Win32_Battery"))
            foreach (ManagementBaseObject m in b.Get())
            {
                s.HasBattery = true;
                if (m["EstimatedChargeRemaining"] is { } c) s.ChargePercent = Convert.ToInt32(c);
            }
        if (!s.HasBattery) return;
        s.DesignCapacity = SumWmi("BatteryStaticData", "DesignedCapacity");
        s.FullChargeCapacity = SumWmi("BatteryFullChargedCapacity", "FullChargedCapacity");
    }

    private static int? SumWmi(string cls, string prop)
    {
        using var q = new ManagementObjectSearcher(@"root\WMI", $"SELECT {prop} FROM {cls}");
        long sum = 0; var any = false;
        foreach (ManagementBaseObject m in q.Get())
            if (m[prop] is { } v) { sum += Convert.ToInt64(v); any = true; }
        return any ? (int)sum : null;
    }

    private static void ReadEvents(PowerState s)
    {
        const long week = 7L * 24 * 3600 * 1000;
        var wakes = new List<string>();
        using (var r = new EventLogReader(new EventLogQuery("System", PathType.LogName,
            $"*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1 and TimeCreated[timediff(@SystemTime) <= {week}]]]")))
            for (var i = 0; i < 500 && r.ReadEvent() is { } e; i++)
                using (e)
                {
                    string? text = null;
                    try { text = e.FormatDescription(); } catch (Exception) { }
                    wakes.Add(PowerTriage.ParseWakeSource(text) ?? "unknown");
                }
        s.WakeSources = wakes;
        using var k = new EventLogReader(new EventLogQuery("System", PathType.LogName,
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= {week}]]]"));
        var n = 0;
        while (k.ReadEvent() is { } e2) { using (e2) n++; }
        s.UnexpectedShutdowns = n;
    }
}
