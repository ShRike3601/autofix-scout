using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Microsoft.Win32;

namespace AutoFixScout.TestBench;

/// <summary>
/// Health-check scenarios (elevated, AFS-TESTBENCH only): create a fault, run the real probe and triage, run the
/// matching rung through FixActions (the executor for admin rungs), check the fault is gone, then ALWAYS put the
/// original state back. Faults that would cut the network or need a restart are not created here.
/// Usage: AutoFixScout.TestBench.exe health [--only id,id] [--long] --out dir
///   --long also runs the slow repairs (DISM, SFC, chkdsk /scan, Visual C++ install, Windows Update reset).
/// </summary>
public static class HealthBench
{
    private static readonly List<string> Report = new();

    private static void Log(string line)
    {
        var l = $"{DateTime.Now:HH:mm:ss} {line}";
        Console.WriteLine(l);
        Report.Add(l);
    }

    private static (IReadOnlyList<Finding> F, IReadOnlyList<Rung> L, HealthState S) Diagnose()
    {
        var s = new RealHealthProbe().Read();
        var f = HealthTriage.Evaluate(s);
        return (f, HealthTriage.Ladder(s, f), s);
    }

    public static async Task<int> RunAsync(string[] args, string outDir)
    {
        var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault()?.Split(',').ToHashSet();
        var runLong = args.Contains("--long");
        var results = new List<(string Id, bool Pass, string Note)>();

        var (bf, bl, bs) = Diagnose();
        Log("Baseline findings:");
        foreach (var f in bf) Log($"  {f.Code}: {f.PlainMessage} [{f.TechnicalDetail}]");
        Log("Baseline ladder: " + string.Join(", ", bl.Select(r => $"{r.ActionId}({r.Arg})")));
        if (bs.ProbeErrors.Count > 0) Log("Probe errors: " + string.Join(" | ", bs.ProbeErrors));
        results.Add(("H0-probe", bs.ProbeErrors.Count == 0, bs.ProbeErrors.Count == 0 ? "all parts read" : string.Join(" | ", bs.ProbeErrors)));

        async Task Scenario(string id, FindingCode expected, string action, Action inject, Action restore)
        {
            if (only != null && !only.Contains(id)) return;
            Log($"--- {id}: expect {expected}, fix with {action}");
            try
            {
                inject();
                var (f, l, _) = Diagnose();
                var hit = f.Any(x => x.Code == expected);
                var rung = l.FirstOrDefault(r => r.ActionId == action);
                Log($"  found: {string.Join(", ", f.Select(x => x.Code))}; rung: {(rung is null ? "MISSING" : rung.Id + "(" + rung.Arg + ")")}");
                if (!hit || rung is null) { results.Add((id, false, "fault not found or rung missing")); return; }
                var (ok, detail) = await FixActions.RunAsync(rung);
                Log($"  ran: ok={ok} {detail}");
                var after = Diagnose().F;
                var gone = after.All(x => x.Code != expected);
                Log($"  after: {string.Join(", ", after.Select(x => x.Code))}");
                results.Add((id, ok && gone, ok && gone ? "fixed" : $"ok={ok} gone={gone} {detail}"));
            }
            catch (Exception ex) { results.Add((id, false, "exception: " + ex.Message)); Log("  EXCEPTION " + ex); }
            finally
            {
                try { restore(); Log("  restored"); }
                catch (Exception ex) { Log("  RESTORE FAILED " + ex.Message); results.Add((id + "-restore", false, ex.Message)); }
            }
        }

        // H1 hosts file redirect
        var hosts = RealHealthProbe.HostsPath;
        var hostsBefore = File.ReadAllBytes(hosts);
        await Scenario("H1-hosts", FindingCode.HealthHostsHijacked, "network.clean_hosts",
            () => File.AppendAllText(hosts, "\r\n1.2.3.4 www.paypal.com\r\n"),
            () => File.WriteAllBytes(hosts, hostsBefore));

        // H2 dead user proxy (a few seconds only)
        const string IS = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        object? pe = null, ps = null;
        await Scenario("H2-proxy", FindingCode.HealthDeadProxy, "network.reset_user_proxy",
            () =>
            {
                using var k = Registry.CurrentUser.OpenSubKey(IS, true)!;
                pe = k.GetValue("ProxyEnable"); ps = k.GetValue("ProxyServer");
                k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                k.SetValue("ProxyServer", "127.0.0.1:9");
            },
            () =>
            {
                using var k = Registry.CurrentUser.OpenSubKey(IS, true)!;
                k.SetValue("ProxyEnable", pe ?? 0, RegistryValueKind.DWord);
                if (ps is string v) k.SetValue("ProxyServer", v); else k.DeleteValue("ProxyServer", false);
            });

        // H3 dead WinHTTP proxy (the bench machine uses direct access)
        await Scenario("H3-winhttp", FindingCode.HealthDeadSystemProxy, "network.reset_winhttp_proxy",
            () => Run("netsh.exe", "winhttp", "set", "proxy", "127.0.0.1:9"),
            () => Run("netsh.exe", "winhttp", "reset", "proxy"));

        // H4 a needed service switched off (Windows Search)
        int? wsStart = null, wsDelayed = null;
        await Scenario("H4-service", FindingCode.HealthServiceOff, "service.restore_default",
            () =>
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WSearch")!;
                wsStart = k.GetValue("Start") as int?; wsDelayed = k.GetValue("DelayedAutostart") as int?;
                Run("sc.exe", "config", "WSearch", "start=", "disabled");
                using var sc = new ServiceController("WSearch");
                if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); }
            },
            () =>
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WSearch", true)!)
                {
                    if (wsStart is { } s) k.SetValue("Start", s, RegistryValueKind.DWord);
                    if (wsDelayed is { } d) k.SetValue("DelayedAutostart", d, RegistryValueKind.DWord);
                }
                using var sc = new ServiceController("WSearch");
                if (sc.Status != ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)); }
            });

        // H5 system PATH missing a Windows folder (only new processes see it)
        const string EnvKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        string? pathBefore = null;
        await Scenario("H5-path", FindingCode.HealthPathDamaged, "system.repair_path",
            () =>
            {
                using var k = Registry.LocalMachine.OpenSubKey(EnvKey, true)!;
                pathBefore = (string)k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)!;
                var damaged = string.Join(";", pathBefore.Split(';').Where(e => !e.TrimEnd('\\').EndsWith(@"System32\Wbem", StringComparison.OrdinalIgnoreCase)));
                k.SetValue("Path", damaged, RegistryValueKind.ExpandString);
            },
            () =>
            {
                if (pathBefore is null) return;
                using var k = Registry.LocalMachine.OpenSubKey(EnvKey, true)!;
                k.SetValue("Path", pathBefore, RegistryValueKind.ExpandString);
            });

        // Repairs that have no fault to create safely: run them and check they finish cleanly.
        async Task Direct(string id, string action, string arg, int tier = 1)
        {
            if (only != null && !only.Contains(id)) return;
            Log($"--- {id}: run {action}({arg})");
            var sw = Stopwatch.StartNew();
            try
            {
                var (ok, detail) = await FixActions.RunAsync(new Rung(id, action, tier, "", arg));
                Log($"  ok={ok} {detail} ({sw.Elapsed.TotalMinutes:0.0} min)");
                results.Add((id, ok, $"{detail} ({sw.Elapsed.TotalMinutes:0.0} min)"));
            }
            catch (Exception ex) { results.Add((id, false, ex.Message)); }
        }

        await Direct("D1-time", "time.resync", "system");
        await Direct("D2-search", "search.rebuild_index", "system");
        if (runLong)
        {
            await Direct("D3-vcredist", "runtime.install_vcredist", HealthTriage.RuntimeArch());
            await Direct("D4-update-reset", "update.reset_components", "system");
            await Direct("D5-chkdsk", "disk.scan", Environment.SystemDirectory[..1]);
            await Direct("D6-dism", "repair.dism", "system");
            await Direct("D7-sfc", "repair.sfc", "system");
        }

        var final = Diagnose();
        Log("Final findings: " + string.Join(", ", final.F.Select(x => x.Code)));

        var md = new StringBuilder($"# Health bench {DateTime.Now:yyyy-MM-dd HH:mm}\n\n| Scenario | Result | Note |\n|---|---|---|\n");
        foreach (var r in results) md.AppendLine($"| {r.Id} | {(r.Pass ? "PASS" : "FAIL")} | {r.Note.Replace("|", "/").Replace("\n", " ")} |");
        md.AppendLine("\n```\n" + string.Join("\n", Report) + "\n```");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"health-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(path, md.ToString());
        Console.WriteLine("Report: " + path);
        return results.All(r => r.Pass) ? 0 : 1;
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe)) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(60_000);
    }
}
