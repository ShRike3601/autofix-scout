// AutoFixScout.TestBench: runs the real-hardware test plan (docs/HANDOFF.md) in batches, without the UI.
// For each scenario: create the fault, run the app's real probe + triage, check the finding and the first rung,
// run the ladder (executor rungs for real; "open Settings / please do X" rungs played by the bench as the person),
// check the machine is healthy, then ALWAYS restore. Writes a Markdown report.
//
// Usage (elevated, AFS-TESTBENCH only):  AutoFixScout.TestBench.exe [sound] [network] [camera] [--out <dir>]
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using AutoFixScout;
using AutoFixScout.Probes;
using AutoFixScout.TestBench;
using AutoFixScout.Triage;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

const string BenchHost = "AFS-TESTBENCH";
if (!Environment.MachineName.Equals(BenchHost, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to run: this is {Environment.MachineName}, not {BenchHost} (CLAUDE.md machine safety rule).");
    return 2;
}

var outIdx = Array.IndexOf(args, "--out");
var outDir = outIdx >= 0 && outIdx + 1 < args.Length ? args[outIdx + 1] : Path.Combine(AppContext.BaseDirectory, "results");
var areas = args.Select(a => a.ToLowerInvariant()).Where(a => a is "sound" or "network" or "camera" or "drivers").ToHashSet();
if (areas.Count == 0) areas = new() { "sound", "network", "camera" };
// drivers: the T2 save point tests (steps 5-6). Needs a newer INF for Intel Display Audio to stage (see DriverTests).
var newerInf = args.SkipWhile(a => a != "--newer-inf").Skip(1).FirstOrDefault();
Warp.MayPause = args.Contains("--pause-warp");
var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault()?.Split(',').ToHashSet();
Directory.CreateDirectory(outDir);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var reportPath = Path.Combine(outDir, $"testbench-{stamp}.md");
Bench.LogPath = Path.Combine(outDir, $"testbench-{stamp}.log");

Bench.Log($"TestBench on {Environment.MachineName}, areas: {string.Join(", ", areas)}");
var env = await Env.DiscoverAsync();
Bench.Log(env.ToString());

var scenarios = Scenarios.All(env).Where(s => areas.Contains(s.Area) && (only is null || only.Contains(s.Id))).ToList();
Failsafe.Arm(env, outDir);
var results = new List<Result>();
try
{
    foreach (var area in scenarios.Select(s => s.Area).Distinct())
    {
        var baseline = await Bench.DiagnoseAsync(area);
        if (baseline.Findings[0].Code != FindingCode.LooksNormal)
        {
            Bench.Log($"[{area}] baseline is not healthy ({baseline.Findings[0].Code}: {baseline.Findings[0].TechnicalDetail}); skipping area");
            results.AddRange(scenarios.Where(s => s.Area == area).Select(s => Result.Skipped(s, $"baseline not healthy: {baseline.Findings[0].Code}")));
            continue;
        }
        foreach (var sc in scenarios.Where(s => s.Area == area))
            results.Add(await Bench.RunAsync(sc));
    }
    if (areas.Contains("drivers")) results.AddRange(await DriverTests.RunAsync(newerInf, only));
}
finally
{
    Failsafe.Disarm();
    File.WriteAllText(reportPath, Report.Build(env, results));
    Bench.Log($"Report: {reportPath}");
}
return results.All(r => r.Verdict is "PASS" or "SKIP") ? 0 : 1;

// ---------------------------------------------------------------------------------------------------------------

/// <summary>Device ids and settings read once at the start, so faults and restores target the real hardware.</summary>
sealed record Env(string? SoundId, string? WifiId, string? WifiAlias, string? WifiProfile, string[] WifiDns, string? CameraId,
    Dictionary<string, string?> Privacy)
{
    public static async Task<Env> DiscoverAsync()
    {
        var audio = new RealAudioProbe().Read();
        var sound = audio.Devices.FirstOrDefault(d => d.InstanceId.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) && d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
                    ?? audio.Devices.FirstOrDefault(d => d.InstanceId.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("Display", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        var net = await new RealNetworkProbe().ReadAsync();
        var wifi = net.Adapters.FirstOrDefault(a => a.IsWireless);
        string? alias = null, profile = null; string[] dns = Array.Empty<string>();
        if (wifi != null)
        {
            alias = Bench.Ps($"(Get-NetAdapter | Where-Object PnPDeviceID -eq '{wifi.InstanceId}').Name").Output.Trim();
            profile = Bench.Ps($"(Get-NetConnectionProfile -InterfaceAlias '{alias}' -ErrorAction SilentlyContinue).Name").Output.Trim();
            dns = Bench.Ps($"(Get-DnsClientServerAddress -InterfaceAlias '{alias}' -AddressFamily IPv4).ServerAddresses -join ','").Output.Trim()
                .Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
        var cam = new RealCameraProbe().Read().Devices.OrderByDescending(d => d.PnpClass == "Camera").FirstOrDefault();
        var privacy = CameraPrivacy.Keys.ToDictionary(k => k, CameraPrivacy.Get);
        return new Env(sound?.InstanceId, wifi?.InstanceId, NullIfEmpty(alias), NullIfEmpty(profile), dns, cam?.InstanceId, privacy);
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public override string ToString() =>
        $"sound={SoundId}\nwifi={WifiId} alias={WifiAlias} profile={(WifiProfile is null ? "(unknown)" : "(known, not written: public repo)")} dns={string.Join(",", WifiDns)}\ncamera={CameraId}\n" +
        string.Join("\n", Privacy.Select(p => $"privacy {p.Key} = {p.Value ?? "(not set)"}"));
}

/// <summary>The three camera privacy switches, as the probe reads them.</summary>
static class CameraPrivacy
{
    const string Webcam = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    public static readonly string[] Keys = { $@"HKLM\{Webcam}", $@"HKCU\{Webcam}", $@"HKCU\{Webcam}\NonPackaged" };

    static RegistryKey Root(string k) => k.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser;
    static string Sub(string k) => k[5..];

    public static string? Get(string k) { using var key = Root(k).OpenSubKey(Sub(k)); return key?.GetValue("Value") as string; }

    public static void Set(string k, string? value)
    {
        using var key = Root(k).CreateSubKey(Sub(k), true);
        if (value is null) key.DeleteValue("Value", false); else key.SetValue("Value", value, RegistryValueKind.String);
    }
}

sealed class Scenario
{
    public required string Id { get; init; }
    public required string Area { get; init; }
    public required string Title { get; init; }
    public required FindingCode Expect { get; init; }
    public required string ExpectFirstRung { get; init; }
    public required Func<Task> Setup { get; init; }
    public required Func<Task> Restore { get; init; }
    /// <summary>What the person does when the expected first rung is "open Settings" or "please do X".</summary>
    public Func<Task>? PersonFix { get; init; }
    public string? MissingReason { get; init; }
}

static class Scenarios
{
    public static IEnumerable<Scenario> All(Env e)
    {
        float? volBefore = null;

        // ---- sound (step 2, regression) ----
        yield return new Scenario
        {
            Id = "2a", Area = "sound", Title = "Speakers muted", Expect = FindingCode.Muted, ExpectFirstRung = "unmute",
            Setup = () => { volBefore = Audio.Set(mute: true); return Task.CompletedTask; },
            Restore = () => { Audio.Set(mute: false, volume: volBefore); return Task.CompletedTask; },
        };
        yield return new Scenario
        {
            Id = "2b", Area = "sound", Title = "Windows Audio service stopped", Expect = FindingCode.AudioServiceStopped, ExpectFirstRung = "restart_services",
            Setup = () => Bench.Service("AudioSrv", run: false),
            Restore = async () => { await Bench.Service("AudioEndpointBuilder", run: true); await Bench.Service("AudioSrv", run: true); },
        };
        yield return new Scenario
        {
            Id = "2c", Area = "sound", Title = "Sound device disabled", Expect = FindingCode.DeviceError, ExpectFirstRung = "restart_services",
            MissingReason = e.SoundId is null ? "no HD Audio sound device found" : null,
            Setup = () => Bench.Pnp("/disable-device", e.SoundId!),
            Restore = () => Bench.Pnp("/enable-device", e.SoundId!),
        };

        // ---- network (step 3) ----
        var noWifi = e.WifiId is null || e.WifiAlias is null ? "no Wi-Fi adapter found" : null;
        Func<Task> reconnect = async () =>
        {
            var sw = Stopwatch.StartNew();
            if (e.WifiProfile != null)
            {
                var (code, output) = Bench.Run("netsh.exe", $"wlan connect name=\"{e.WifiProfile}\" interface=\"{e.WifiAlias}\"");
                Bench.Log($"  netsh wlan connect exit {code}: {output.Trim()}");
            }
            // How long a reconnect takes on this network matters for the app's own retry window (ConnectionTestAsync).
            while (sw.Elapsed < TimeSpan.FromSeconds(120))
            {
                var up = Bench.Ps($"(Get-NetAdapter -Name '{e.WifiAlias}').Status").Output.Trim();
                if (up == "Up") { Bench.Log($"  Wi-Fi adapter Up after {sw.Elapsed.TotalSeconds:0}s"); break; }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            var internet = Stopwatch.StartNew();
            while (internet.Elapsed < TimeSpan.FromSeconds(120) && (await new RealNetworkProbe().ReadAsync()).InternetReachable != Tri.Yes)
                await Task.Delay(TimeSpan.FromSeconds(2));
            Bench.Log($"  internet reachable {sw.Elapsed.TotalSeconds:0}s after the connect command");
        };
        yield return new Scenario
        {
            Id = "3b", Area = "network", Title = "Wi-Fi adapter disabled", Expect = FindingCode.NetAdapterDisabled, ExpectFirstRung = "enable_adapter",
            MissingReason = noWifi,
            Setup = () => Bench.Pnp("/disable-device", e.WifiId!),
            Restore = async () => { await Bench.Pnp("/enable-device", e.WifiId!); await reconnect(); },
        };
        yield return new Scenario
        {
            Id = "3c", Area = "network", Title = "Wi-Fi disconnected", Expect = FindingCode.NotConnected, ExpectFirstRung = "pick_wifi",
            MissingReason = noWifi ?? (e.WifiProfile is null ? "Wi-Fi profile name unknown, can't reconnect afterwards" : null),
            Setup = async () => { Bench.Run("netsh.exe", $"wlan disconnect interface=\"{e.WifiAlias}\""); await Task.Delay(TimeSpan.FromSeconds(5)); },
            PersonFix = reconnect,
            Restore = reconnect,
        };
        yield return new Scenario
        {
            Id = "3d", Area = "network", Title = "Bogus DNS server on Wi-Fi", Expect = FindingCode.DnsFailing, ExpectFirstRung = "flush_dns",
            // Cloudflare WARP answers DNS itself, which hides a bad DNS server; --pause-warp disconnects it for this scenario only.
            MissingReason = noWifi ?? (Warp.Connected() && !Warp.MayPause ? "Cloudflare WARP is connected and hides DNS faults (run with --pause-warp)" : null),
            Setup = async () =>
            {
                if (Warp.MayPause && Warp.Connected()) { Warp.Set(connect: false); await Task.Delay(TimeSpan.FromSeconds(8)); }
                // WARP's 127.0.2.x DNS only answers while WARP is connected, so the bogus server goes first and alone.
                Bench.Ps($"Set-DnsClientServerAddress -InterfaceAlias '{e.WifiAlias}' -ServerAddresses 192.0.2.1; Clear-DnsClientCache");
            },
            Restore = async () =>
            {
                // While WARP is off its 127.0.2.x addresses don't answer, so put back automatic DNS; WARP sets its own on connect.
                var original = e.WifiDns.Length == 0 || Warp.WasPausedByBench || e.WifiDns.All(d => d.StartsWith("127."))
                    ? $"Set-DnsClientServerAddress -InterfaceAlias '{e.WifiAlias}' -ResetServerAddresses"
                    : $"Set-DnsClientServerAddress -InterfaceAlias '{e.WifiAlias}' -ServerAddresses {string.Join(",", e.WifiDns.Select(d => $"'{d}'"))}";
                Bench.Ps(original + "; Clear-DnsClientCache");
                if (Warp.WasPausedByBench) { Warp.Set(connect: true); await Task.Delay(TimeSpan.FromSeconds(10)); }
            },
        };

        // ---- camera (step 4) ----
        var labels = new[] { "4a", "4b", "4c" };
        var names = new[] { "Camera access", "Let apps access your camera", "Let desktop apps access your camera" };
        for (var i = 0; i < CameraPrivacy.Keys.Length; i++)
        {
            var key = CameraPrivacy.Keys[i];
            Func<Task> put = () => { CameraPrivacy.Set(key, e.Privacy[key]); return Task.CompletedTask; };
            yield return new Scenario
            {
                Id = labels[i], Area = "camera", Title = $"Privacy switch off: \"{names[i]}\"", Expect = FindingCode.CameraPrivacyOff, ExpectFirstRung = "privacy",
                Setup = () => { CameraPrivacy.Set(key, "Deny"); return Task.CompletedTask; },
                PersonFix = put,
                Restore = put,
            };
        }
        // 4d (another app holding the camera) isn't automated: Windows 26300 no longer reports live camera use where a
        // non-admin app can read it, and Windows shares the camera between apps. The ladder asks the person instead.
        yield return new Scenario
        {
            Id = "4e", Area = "camera", Title = "Camera device disabled", Expect = FindingCode.CameraDisabled, ExpectFirstRung = "enable_camera",
            MissingReason = e.CameraId is null ? "no camera found" : null,
            Setup = () => Bench.Pnp("/disable-device", e.CameraId!),
            Restore = () => Bench.Pnp("/enable-device", e.CameraId!),
        };
        yield return new Scenario
        {
            // Device Manager > Uninstall device (driver kept): the camera vanishes until a hardware scan.
            Id = "4f", Area = "camera", Title = "Camera uninstalled (driver kept)", Expect = FindingCode.NoCamera, ExpectFirstRung = "rescan",
            MissingReason = e.CameraId is null ? "no camera found" : null,
            Setup = () => Bench.Pnp("/remove-device", e.CameraId!),
            Restore = () => Bench.ScanDevices(),
        };
        yield return new Scenario
        {
            Id = "2e", Area = "sound", Title = "Sound device uninstalled (driver kept)", Expect = FindingCode.NoActiveOutput, ExpectFirstRung = "restart_services",
            MissingReason = e.SoundId is null ? "no HD Audio sound device found" : null,
            Setup = () => Bench.Pnp("/remove-device", e.SoundId!),
            Restore = () => Bench.ScanDevices(),
        };
    }
}

/// <summary>Plays the person: says yes to T0/T1, stops before T2 (driver changes are tested separately, with a person).</summary>
sealed class BenchFixHost(Scenario sc, List<string> trail) : IFixHost
{
    public Task<bool> ConfirmAsync(Rung rung)
    {
        var yes = rung.Tier < 2;
        trail.Add($"offered {rung.Id} (T{rung.Tier}) -> {(yes ? "yes" : "NOT NOW (bench stops before T2)")}");
        return Task.FromResult(yes);
    }

    public async Task<(bool Ok, string Detail)> RunAsync(Rung rung)
    {
        if (rung.ActionId.StartsWith("ui."))
        {
            // Don't pop Settings pages during a batch; the bench does what the instruction asks, but only for the
            // rung the scenario expects, so a wrong rung can't be "fixed" by accident.
            if (rung.Id == sc.ExpectFirstRung && sc.PersonFix != null) { await sc.PersonFix(); return (true, "bench played the person: followed the instruction"); }
            return (true, "bench played the person: nothing to do for this instruction");
        }
        var sw = Stopwatch.StartNew();
        var r = await FixActions.RunAsync(rung);
        trail.Add($"ran {rung.Id}: ok={r.Ok} ({r.Detail}) in {sw.Elapsed.TotalSeconds:0.0}s");
        return r;
    }

    public async Task<bool> TestAsync()
    {
        var ok = await Bench.WaitHealthyAsync(sc.Area);
        trail.Add($"real test (bench: re-probe until LooksNormal) -> {(ok ? "works" : "still broken")}");
        return ok;
    }

    public Task<bool> SettleAsync(Rung rung, bool keep) => Task.FromResult(true);
}

sealed record Result(Scenario S, string Verdict, string Why, FindingCode? Found, string? FoundDetail, string? FirstRung,
    IReadOnlyList<string> Ladder, Outcome? Outcome, IReadOnlyList<string> Trail, bool? HealthyAfter, TimeSpan Took)
{
    public static Result Skipped(Scenario s, string why) => new(s, "SKIP", why, null, null, null, Array.Empty<string>(), null, Array.Empty<string>(), null, TimeSpan.Zero);
}

static class Bench
{
    public static string LogPath = "testbench.log";

    public static void Log(string line)
    {
        var l = $"{DateTime.Now:HH:mm:ss} {line}";
        Console.WriteLine(l);
        File.AppendAllText(LogPath, l + Environment.NewLine);
    }

    public static async Task<Diagnosis> DiagnoseAsync(string area)
    {
        switch (area)
        {
            case "sound":
                var a = new RealAudioProbe().Read(); var fa = AudioTriage.Evaluate(a);
                return new Diagnosis(a, fa, AudioLadder.For(a, fa), AudioLadder.ProbablyHardware(a));
            case "network":
                var n = await new RealNetworkProbe().ReadAsync(); var fn = NetworkTriage.Evaluate(n);
                return new Diagnosis(n, fn, NetworkTriage.Ladder(n, fn), NetworkTriage.ProbablyHardware(n));
            default:
                var c = new RealCameraProbe().Read(); var fc = CameraTriage.Evaluate(c);
                return new Diagnosis(c, fc, CameraTriage.Ladder(c, fc), CameraTriage.ProbablyHardware(c));
        }
    }

    public static async Task<bool> WaitHealthyAsync(string area)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(area == "network" ? 60 : 20);
        while (true)
        {
            if ((await DiagnoseAsync(area)).Findings[0].Code == FindingCode.LooksNormal) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    public static async Task<Result> RunAsync(Scenario sc)
    {
        var sw = Stopwatch.StartNew();
        var trail = new List<string>();
        Log($"[{sc.Id}] {sc.Title}");
        if (sc.MissingReason != null) { Log($"[{sc.Id}] SKIP: {sc.MissingReason}"); return Result.Skipped(sc, sc.MissingReason); }
        Diagnosis? before = null; Outcome? outcome = null; bool? healthy = null;
        string verdict, why;
        try
        {
            await sc.Setup();
            before = await DiagnoseAsync(sc.Area);
            var top = before.Findings[0];
            Log($"[{sc.Id}] found {top.Code} ({top.TechnicalDetail}); ladder: {string.Join(" > ", before.Ladder.Select(r => r.Id))}");
            if (top.Code == FindingCode.LooksNormal)
            {
                verdict = "SKIP"; why = "fault could not be created (machine still looks normal)";
            }
            else
            {
                outcome = await LadderRunner.RunAsync(before.Ladder, new BenchFixHost(sc, trail), new List<StepLog>());
                var problems = new List<string>();
                if (top.Code != sc.Expect) problems.Add($"found {top.Code}, expected {sc.Expect}");
                var first = before.Ladder.FirstOrDefault()?.Id;
                if (first != sc.ExpectFirstRung) problems.Add($"first fix {first ?? "(none)"}, expected {sc.ExpectFirstRung}");
                if (outcome != Outcome.Fixed) problems.Add($"outcome {outcome}");
                verdict = problems.Count == 0 ? "PASS" : "FAIL";
                why = problems.Count == 0 ? "" : string.Join("; ", problems);
            }
        }
        catch (Exception ex) { verdict = "ERROR"; why = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            try { await sc.Restore(); } catch (Exception ex) { trail.Add($"RESTORE FAILED: {ex.Message}"); }
        }
        healthy = await WaitHealthyAsync(sc.Area);
        if (healthy == false) { verdict = verdict == "PASS" ? "FAIL" : verdict; why = (why.Length > 0 ? why + "; " : "") + "machine NOT healthy after restore"; }
        foreach (var t in trail) Log($"[{sc.Id}]   {t}");
        Log($"[{sc.Id}] {verdict} {why} (outcome {outcome}, healthy after: {healthy}, {sw.Elapsed.TotalSeconds:0}s)");
        var f = before?.Findings[0];
        return new Result(sc, verdict, why, f?.Code, f?.TechnicalDetail, before?.Ladder.FirstOrDefault()?.Id,
            before?.Ladder.Select(r => $"{r.Id} (T{r.Tier})").ToList() ?? new List<string>(), outcome, trail, healthy, sw.Elapsed);
    }

    public static Task Pnp(string verb, string instanceId)
    {
        var (code, output) = Run("pnputil.exe", $"{verb} \"{instanceId}\"");
        // 3010 = done, reboot suggested. "already enabled/disabled" = the app's fix already did it, which is fine.
        if (code is not (0 or 3010) && !output.Contains("already", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"pnputil {verb} exit {code}: {output.Trim()}");
        return Task.Delay(TimeSpan.FromSeconds(4)); // let PnP and the endpoints settle
    }

    /// <summary>Restore for the "uninstalled" scenarios; harmless if the app's rescan already brought the device back.</summary>
    public static Task ScanDevices()
    {
        var (code, output) = Run("pnputil.exe", "/scan-devices");
        if (code != 0) throw new InvalidOperationException($"pnputil /scan-devices exit {code}: {output.Trim()}");
        return Task.Delay(TimeSpan.FromSeconds(6));
    }

    public static async Task Service(string name, bool run)
    {
        using var sc = new ServiceController(name);
        if (run && sc.Status != ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
        if (!run && sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    public static (int Code, string Output) Run(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    public static (int Code, string Output) Ps(string script) =>
        Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"");
}

/// <summary>
/// Steps 5-6: T2 driver changes through the app's own FixActions (save point, change, post-check, settle/undo), which
/// start the real elevated executor. Target: Intel Display Audio (the unused HDMI audio output), never the speakers.
/// A newer intcdaud.inf is staged first so the installed one becomes "previous", which is what rollback needs.
/// </summary>
static class DriverTests
{
    const string DisplayAudioHwid = @"HDAUDIO\FUNC_01&VEN_8086&DEV_280B";

    static Scenario Sc(string id, string title) => new()
    {
        Id = id, Area = "drivers", Title = title, Expect = FindingCode.LooksNormal, ExpectFirstRung = "-",
        Setup = () => Task.CompletedTask, Restore = () => Task.CompletedTask,
    };

    static string? Version(string instanceId) => AutoFixScout.Drivers.DriverStatus.For(instanceId)?.Version;

    static string PendingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "pending-savepoints");
    static string SavePointDir(string id) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFixScout", "savepoints", id);

    public static async Task<List<Result>> RunAsync(string? newerInf, HashSet<string>? only)
    {
        var results = new List<Result>();
        var audio = new RealAudioProbe().Read();
        var da = audio.Devices.FirstOrDefault(d => d.InstanceId.StartsWith(DisplayAudioHwid, StringComparison.OrdinalIgnoreCase));
        // --only unstage: put the original driver back and delete every staged copy of the newer package (after a manual test).
        if (da != null && only?.Contains("unstage") == true)
        {
            // Only undo what "stage" wrote down; never guess which package is the original.
            var record = PnpDrivers.StageRecord.Parse(File.Exists(StageRecordPath) ? File.ReadAllText(StageRecordPath) : null);
            var store = PnpDrivers.Parse(Bench.Run("pnputil.exe", "/enum-drivers /class Media").Output);
            var delete = PnpDrivers.ToDelete(store, record);
            if (delete is null)
            {
                Bench.Log($"[drivers] unstage: nothing safe to delete (record: {record?.Serialize().Replace('\n', ' ') ?? "none"}); left as is");
                return results;
            }
            if (!string.Equals(CurrentInf(da.InstanceId), record!.OriginalOem, StringComparison.OrdinalIgnoreCase))
                ForceInstall(da.InstanceId, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", record.OriginalOem));
            Bench.Log($"[drivers] unstage {delete}: exit {Bench.Run("pnputil.exe", $"/delete-driver {delete} /uninstall /force").Code}");
            File.Delete(StageRecordPath);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Bench.Log($"[drivers] Intel Display Audio now {Version(da.InstanceId)} ({CurrentInf(da.InstanceId)})");
            return results;
        }
        if (da is null || newerInf is null || !File.Exists(newerInf))
        {
            results.Add(Result.Skipped(Sc("5", "Driver rollback"), da is null ? "no Intel Display Audio device" : "--newer-inf missing"));
            return results;
        }
        var original = Version(da.InstanceId);
        Bench.Log($"[drivers] Intel Display Audio {da.InstanceId} at {original}");

        // Stage the newer package and make it current: same as "Windows Update just installed a newer driver".
        var originalInf = CurrentInf(da.InstanceId);
        var (addCode, addOut) = Bench.Run("pnputil.exe", $"/add-driver \"{newerInf}\" /install");
        var newOem = System.Text.RegularExpressions.Regex.Match(addOut, @"oem\d+\.inf", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
        // Windows keeps the installed driver when it matches the device more specifically (seen here), so force it,
        // as "Update driver > Have disk" would.
        ForceInstall(da.InstanceId, newerInf);
        await Task.Delay(TimeSpan.FromSeconds(5));
        var newer = Version(da.InstanceId);
        Bench.Log($"[drivers] staged {newOem} (pnputil exit {addCode}), forced onto the device; device now at {newer} (was {original}, {originalInf})");
        // --only stage: leave the newer driver in place for a manual test in the app; "--only unstage" undoes it.
        if (only?.Contains("stage") == true)
        {
            if (newOem.Length > 0 && originalInf != null && !newOem.Equals(originalInf, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StageRecordPath)!);
                File.WriteAllText(StageRecordPath, new PnpDrivers.StageRecord(originalInf, newOem).Serialize());
            }
            return results;
        }

        try
        {
            if (Want(only, "5a")) results.Add(await RollbackAsync("5a", "Rollback, person says it works: older driver stays", da.InstanceId, da.Name, keep: true, original, newer, newerInf));
            if (Want(only, "5b")) results.Add(await RollbackAsync("5b", "Rollback, person says it doesn't: newer driver comes back", da.InstanceId, da.Name, keep: false, original, newer, newerInf));
            if (Want(only, "6"))
            {
                var realtek = audio.Devices.FirstOrDefault(d => d.InstanceId.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) && d.Name.Contains("Realtek"));
                if (realtek != null) results.Add(await WindowsUpdateNothingOfferedAsync(realtek.InstanceId));
            }
        }
        finally
        {
            // Put the laptop back: remove the staged package; Windows reinstalls the best remaining driver (the original).
            if (newOem.Length > 0)
            {
                if (Version(da.InstanceId) != original && originalInf != null)
                    try { ForceInstall(da.InstanceId, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", originalInf)); } catch (Exception ex) { Bench.Log($"[drivers] put-back failed: {ex.Message}"); }
                var (code, output) = Bench.Run("pnputil.exe", $"/delete-driver {newOem} /uninstall /force");
                await Task.Delay(TimeSpan.FromSeconds(5));
                Bench.Log($"[drivers] removed {newOem} (exit {code}); device now at {Version(da.InstanceId)} (originally {original}). {output.Trim().Split('\n').LastOrDefault()}");
            }
        }
        return results;
    }

    static string StageRecordPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AFS-TestBench", "driver-stage.txt");

    static bool Want(HashSet<string>? only, string id) => only is null || only.Contains(id);

    static async Task MakeCurrentAsync(string instanceId, string inf, string? version)
    {
        if (Version(instanceId) == version) return;
        ForceInstall(instanceId, inf);
        await Task.Delay(TimeSpan.FromSeconds(5));
    }

    // Same live source as the app (WMI lags real changes).
    static string? CurrentInf(string instanceId)
    {
        using var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
        if (enumKey?.GetValue("Driver") is not string classKey) return null;
        using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{classKey}");
        return cls?.GetValue("InfPath") as string;
    }

    [System.Runtime.InteropServices.DllImport("newdev.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool reboot);

    /// <summary>Installs this INF on the device even if Windows ranks the current driver higher (INSTALLFLAG_FORCE | NONINTERACTIVE).</summary>
    static void ForceInstall(string instanceId, string infPath)
    {
        using var q = new System.Management.ManagementObjectSearcher($"SELECT HardwareID FROM Win32_PnPEntity WHERE DeviceID = '{instanceId.Replace(@"\", @"\\")}'");
        var ids = q.Get().Cast<System.Management.ManagementBaseObject>().SelectMany(d => d["HardwareID"] as string[] ?? Array.Empty<string>()).ToList();
        var text = File.ReadAllText(infPath);
        var hwid = ids.FirstOrDefault(h => text.Contains(h, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"{Path.GetFileName(infPath)} names none of the device's hardware IDs");
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwid, infPath, 0x1 | 0x4, out _))
            throw new InvalidOperationException($"UpdateDriverForPlugAndPlayDevices failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        Bench.Log($"  forced {Path.GetFileName(infPath)} onto the device via {hwid}");
    }

    static async Task<Result> RollbackAsync(string id, string title, string instanceId, string name, bool keep, string? original, string? newer, string newerInf)
    {
        var sw = Stopwatch.StartNew();
        var trail = new List<string>();
        var problems = new List<string>();
        await MakeCurrentAsync(instanceId, newerInf, newer);
        var status = AutoFixScout.Drivers.DriverStatus.For(instanceId);
        trail.Add($"before: {status?.Version}, previous in store: {status?.PreviousVersion}, staged {status?.StagedUtc:u}");
        var rung = DriverRules.Rollback("sound", name, instanceId, status);
        if (rung is null) problems.Add("rollback not offered");
        else
        {
            trail.Add($"offered: {rung.Say}");
            var (ok, detail, sp) = await FixActions.ChangeDriverAsync(rung.ActionId, instanceId);
            trail.Add($"change: ok={ok} {detail}");
            var after = Version(instanceId);
            trail.Add($"after change: {after}");
            if (!ok || after != original) problems.Add($"rollback gave {after}, expected {original}");
            if (sp != null)
            {
                trail.Add($"save point folder exists: {Directory.Exists(SavePointDir(sp))}, manifest: {File.Exists(Path.Combine(SavePointDir(sp), "savepoint.json"))}, " +
                          $"exported INF: {Directory.Exists(Path.Combine(SavePointDir(sp), "driver")) && Directory.EnumerateFiles(Path.Combine(SavePointDir(sp), "driver"), "*.inf", SearchOption.AllDirectories).Any()}");
                trail.Add($"open marker before settle: {File.Exists(Path.Combine(PendingDir, sp))}");
                var settled = await FixActions.SettleSavePointAsync(sp, keep);
                var final = Version(instanceId);
                trail.Add($"settle keep={keep}: {settled}; driver now {final}; marker left: {File.Exists(Path.Combine(PendingDir, sp))}");
                var expected = keep ? original : newer;
                if (!settled || final != expected) problems.Add($"after settle {final}, expected {expected}");
                if (File.Exists(Path.Combine(PendingDir, sp))) problems.Add("pending marker not removed");
            }
            else problems.Add("no save point id returned");
        }
        foreach (var t in trail) Bench.Log($"[{id}]   {t}");
        var verdict = problems.Count == 0 ? "PASS" : "FAIL";
        Bench.Log($"[{id}] {verdict} {string.Join("; ", problems)}");
        return new Result(Sc(id, title), verdict, string.Join("; ", problems), null, null, rung?.Id, Array.Empty<string>(), null, trail, null, sw.Elapsed);
    }

    static async Task<Result> WindowsUpdateNothingOfferedAsync(string instanceId)
    {
        var sw = Stopwatch.StartNew();
        var before = Version(instanceId);
        var (ok, detail, sp) = await FixActions.ChangeDriverAsync("driver.install_from_windows_update", instanceId);
        var after = Version(instanceId);
        var trail = new List<string> { $"Realtek before {before}, after {after}", $"result: ok={ok} {detail}, save point {sp ?? "none"}" };
        var problems = new List<string>();
        if (ok || sp != null) problems.Add("expected 'nothing offered' (exit 3)");
        if (after != before) problems.Add("driver changed");
        foreach (var t in trail) Bench.Log($"[6]   {t}");
        var verdict = problems.Count == 0 ? "PASS" : "FAIL";
        Bench.Log($"[6] {verdict} {string.Join("; ", problems)}");
        return new Result(Sc("6", "Windows Update driver: nothing offered for Realtek -> nothing changed"), verdict, string.Join("; ", problems),
            null, null, null, Array.Empty<string>(), null, trail, null, sw.Elapsed);
    }
}

/// <summary>Cloudflare WARP on the test laptop. Only paused with --pause-warp, and always reconnected afterwards.</summary>
static class Warp
{
    const string Cli = @"C:\Program Files\Cloudflare\Cloudflare WARP\warp-cli.exe";
    public static bool MayPause;
    public static bool WasPausedByBench;

    public static bool Connected()
    {
        if (!File.Exists(Cli)) return false;
        var status = Bench.Run(Cli, "status").Output;
        return status.Contains("Connected") && !status.Contains("Disconnected");
    }

    public static void Set(bool connect)
    {
        if (!connect)
        {
            var (code, output) = Bench.Run(Cli, "disconnect");
            Bench.Log($"  warp-cli disconnect exit {code}: {output.Trim()}");
            WasPausedByBench = true;
            return;
        }
        // In batch 2 one "connect" reported Success but WARP stayed disconnected, so retry until it says Connected.
        var sw = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= 4 && !Connected(); attempt++)
        {
            var (code, output) = Bench.Run(Cli, "connect");
            Bench.Log($"  warp-cli connect (attempt {attempt}) exit {code}: {output.Trim()}");
            for (var i = 0; i < 8 && !Connected(); i++) Thread.Sleep(2000);
        }
        var ok = Connected();
        Bench.Log(ok ? $"  WARP connected after {sw.Elapsed.TotalSeconds:0}s" : "  WARNING: WARP still not connected; the failsafe task will retry");
        WasPausedByBench = !ok;
    }
}

static class Audio
{
    /// <summary>Sets mute (and volume if given) on the default output; returns the volume before.</summary>
    public static float Set(bool mute, float? volume = null)
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var v = dev.AudioEndpointVolume;
        var before = v.MasterVolumeLevelScalar;
        v.Mute = mute;
        if (volume is { } vol) v.MasterVolumeLevelScalar = vol;
        return before;
    }
}

/// <summary>
/// If the bench dies mid-scenario (crash, power), a one-time scheduled task puts the devices and settings back 30 minutes
/// after the start. Removed when the bench finishes normally.
/// </summary>
static class Failsafe
{
    const string Task = "AFS-TestBench-Failsafe";

    public static void Arm(Env e, string dir)
    {
        var ps = new StringBuilder("# Written by AutoFixScout.TestBench: puts the test laptop back if a batch died halfway.\r\n");
        ps.AppendLine("pnputil /scan-devices"); // brings back a device a "removed" scenario uninstalled
        foreach (var id in new[] { e.SoundId, e.WifiId, e.CameraId }.OfType<string>()) ps.AppendLine($"pnputil /enable-device \"{id}\"");
        ps.AppendLine("Start-Service AudioEndpointBuilder, AudioSrv -ErrorAction SilentlyContinue");
        if (Warp.MayPause) ps.AppendLine("& 'C:\\Program Files\\Cloudflare\\Cloudflare WARP\\warp-cli.exe' connect");
        if (e.WifiAlias != null)
            ps.AppendLine(e.WifiDns.Length == 0
                ? $"Set-DnsClientServerAddress -InterfaceAlias '{e.WifiAlias}' -ResetServerAddresses"
                : $"Set-DnsClientServerAddress -InterfaceAlias '{e.WifiAlias}' -ServerAddresses {string.Join(",", e.WifiDns.Select(d => $"'{d}'"))}");
        if (e.WifiProfile != null) ps.AppendLine($"netsh wlan connect name=\"{e.WifiProfile}\" interface=\"{e.WifiAlias}\"");
        foreach (var (k, v) in e.Privacy)
        {
            var path = (k.StartsWith("HKLM") ? "HKLM:" : "HKCU:") + k[4..];
            ps.AppendLine(v is null ? $"Remove-ItemProperty -Path '{path}' -Name Value -ErrorAction SilentlyContinue"
                                    : $"New-Item -Path '{path}' -Force | Out-Null; Set-ItemProperty -Path '{path}' -Name Value -Value '{v}'");
        }
        ps.AppendLine($"schtasks /delete /tn {Task} /f");
        // Kept outside the repo: it names the Wi-Fi network, and the repo is public.
        var safeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AFS-TestBench");
        Directory.CreateDirectory(safeDir);
        var file = Path.Combine(safeDir, "failsafe-restore.ps1");
        File.WriteAllText(file, ps.ToString());
        var at = DateTime.Now.AddMinutes(30);
        var (code, output) = Bench.Run("schtasks.exe",
            $"/create /tn {Task} /tr \"powershell -NoProfile -ExecutionPolicy Bypass -File \\\"{file}\\\"\" /sc once /st {at:HH:mm} /rl highest /it /f");
        Bench.Log(code == 0 ? $"failsafe armed for {at:HH:mm}: {file}" : $"WARNING: failsafe task not created (exit {code}): {output.Trim()}");
    }

    public static void Disarm() => Bench.Run("schtasks.exe", $"/delete /tn {Task} /f");
}

static class Report
{
    public static string Build(Env env, List<Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# TestBench run {DateTime.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine($"{results.Count(r => r.Verdict == "PASS")} pass, {results.Count(r => r.Verdict == "FAIL")} fail, " +
                      $"{results.Count(r => r.Verdict == "ERROR")} error, {results.Count(r => r.Verdict == "SKIP")} skipped.");
        sb.AppendLine();
        sb.AppendLine("| Step | Fault | Found | First fix | Outcome | Healthy after | Verdict |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.S.Id} | {r.S.Title} | {r.Found?.ToString() ?? "-"} | {r.FirstRung ?? "-"} | {r.Outcome?.ToString() ?? "-"} | " +
                          $"{(r.HealthyAfter is null ? "-" : r.HealthyAfter.Value ? "yes" : "**NO**")} | **{r.Verdict}** {r.Why} |");
        sb.AppendLine();
        sb.AppendLine("## Details");
        foreach (var r in results)
        {
            sb.AppendLine();
            sb.AppendLine($"### {r.S.Id} {r.S.Title} ({r.Took.TotalSeconds:0}s)");
            sb.AppendLine($"- expected: {r.S.Expect}, first fix {r.S.ExpectFirstRung}");
            if (r.Found != null) sb.AppendLine($"- found: {r.Found} -- {r.FoundDetail}");
            if (r.Ladder.Count > 0) sb.AppendLine($"- ladder: {string.Join(" > ", r.Ladder)}");
            foreach (var t in r.Trail) sb.AppendLine($"- {t}");
        }
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine("```");
        sb.AppendLine(env.ToString());
        sb.AppendLine("```");
        return sb.ToString();
    }
}
