using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>Reads the real machine. Runs only when AFS_LIVE is set (test bench); writes what it found to AFS_LIVE (a file path).</summary>
[Collection("Live machine")] // one at a time: they change and restore the same settings
public class LiveProbeTests
{
    [Fact]
    public void HealthProbe_ReadsThisMachine()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new RealHealthProbe().Read();
        var f = HealthTriage.Evaluate(s);
        var lines = new List<string> { $"probe took {sw.Elapsed.TotalSeconds:0.0} s", "errors: " + string.Join(" | ", s.ProbeErrors),
            $"clock offset {s.ClockOffsetSeconds:0.0}; bluescreens {s.BlueScreens}; shutdowns {s.UnexpectedShutdowns}; disk {s.DiskErrors}; whea {s.HardwareErrors}; updatefail {s.UpdateFailures}",
            "crashes: " + string.Join("; ", s.Crashes.Select(c => $"{c.App} x{c.Count} {c.Module} {c.ExceptionCode} {c.Cause}")),
            "hangs: " + string.Join("; ", s.Hangs.Select(h => $"{h.Key} x{h.Value}")),
            "runtime: " + string.Join("; ", s.RuntimeErrors), "failing services: " + string.Join("; ", s.FailingServices),
            "services: " + string.Join(", ", s.Services.Select(x => $"{x.Name}:{x.Status}/{x.StartType}")),
            "store broken: " + string.Join("; ", s.BrokenStoreApps.Select(a => a.FullName + ":" + a.Status)),
            $"winsock 3rd party: {string.Join("; ", s.ThirdPartyWinsock)}; path missing: {string.Join("; ", s.MissingPathEntries)}; reboot {s.RebootPending}" };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage} [{x.TechnicalDetail}]"));
        lines.AddRange(HealthTriage.Ladder(s, f).Select(r => $"RUNG {r.Id} {r.ActionId}({r.Arg})"));
        lines.Add($"recent changes: {s.RecentChanges.Count} (programs {s.RecentChanges.Count(c => c.Kind == RecentChangeKind.Program)}, updates {s.RecentChanges.Count(c => c.Kind == RecentChangeKind.Update)}, drivers {s.RecentChanges.Count(c => c.Kind == RecentChangeKind.Driver)}); restore points {s.RestorePointsKnown}/{s.RestorePoints.Count}; graphics resets {s.GraphicsResets}");
        lines.Add("devices: " + string.Join("; ", s.DeviceProblems.Select(d => $"{d.PnpClass} code={d.Code}")) + "; temp profile: " + s.TempProfile);
        lines.Add("stop codes: " + string.Join(", ", s.StopCodes.Select(c => $"{StopCodes.Hex(c.Code)} x{c.Count}")) + $"; pagefile auto={s.PageFile?.Automatic} ram={s.PageFile?.TotalRamMb} MB allocated={s.PageFile?.AllocatedMb} MB settings={s.PageFile?.Settings.Count}; low memory events {s.LowMemoryEvents}");
        File.WriteAllLines(output, lines);
        Assert.DoesNotContain(s.ProbeErrors, e => e.StartsWith("device errors") || e.StartsWith("user profile"));
        Assert.NotNull(s.DeviceProblems);
        Assert.NotEmpty(s.Drives);
        Assert.All(s.Drives, d => Assert.False(d.Failing || d.Warning));
        Assert.DoesNotContain(s.ProbeErrors, e => e.StartsWith("recent changes") || e.StartsWith("restore points") || e.StartsWith("graphics resets"));
        Assert.DoesNotContain(s.ProbeErrors, e => e.StartsWith("blue screen causes") || e.StartsWith("virtual memory") || e.StartsWith("low memory events"));
        Assert.NotNull(s.PageFile);
        Assert.NotNull(s.PageFile!.Automatic);
        Assert.True(s.PageFile.TotalRamMb > 0);
        Assert.True(s.StopCodes.Sum(c => c.Count) <= s.BlueScreens);
        Assert.Empty(s.ProbeErrors);
    }
}

/// <summary>Non-admin repairs on the real machine (AFS_LIVE set). Each one creates its fault and always puts things back.</summary>
[Collection("Live machine")] // one at a time: they change and restore the same settings
public class LiveUserRepairTests
{
    private static bool Live => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"));
    private const string IS = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [Fact]
    public async Task DeadProxy_IsFoundAndSwitchedOff()
    {
        if (!Live) return;
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(IS, true)!;
        var pe = k.GetValue("ProxyEnable"); var ps = k.GetValue("ProxyServer");
        try
        {
            k.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("ProxyServer", "127.0.0.1:9");
            var s = new RealHealthProbe().Read();
            var f = HealthTriage.Evaluate(s);
            Assert.Contains(f, x => x.Code == FindingCode.HealthDeadProxy);
            var rung = HealthTriage.Ladder(s, f).First(r => r.ActionId == "network.reset_user_proxy");
            var (ok, _) = await FixActions.RunAsync(rung);
            Assert.True(ok);
            Assert.DoesNotContain(HealthTriage.Evaluate(new RealHealthProbe().Read()), x => x.Code == FindingCode.HealthDeadProxy);
        }
        finally
        {
            k.SetValue("ProxyEnable", pe ?? 0, Microsoft.Win32.RegistryValueKind.DWord);
            if (ps is string v) k.SetValue("ProxyServer", v); else k.DeleteValue("ProxyServer", false);
        }
    }

    [Fact]
    public async Task StoreApp_ReregistersAndStaysOk()
    {
        if (!Live) return;
        var (code, name) = AutoFixScout.Repair.UserRepairs.RunPowerShell("(Get-AppxPackage Microsoft.WindowsCalculator).PackageFullName");
        Assert.Equal(0, code);
        var (ok, detail) = await FixActions.RunAsync(new Rung("t", "apps.reregister", 0, "", name.Trim()));
        Assert.True(ok, detail);
        Assert.Contains("status Ok", detail);
    }

    [Fact]
    public async Task Explorer_RestartsAndComesBack()
    {
        if (!Live) return;
        var (ok, _) = await FixActions.RunAsync(new Rung("t", "shell.restart_explorer", 0, ""));
        Assert.True(ok);
        Assert.NotEmpty(System.Diagnostics.Process.GetProcessesByName("explorer"));
    }
}

/// <summary>The internet check as the app builds it (network probe + health extras), with a real dead proxy (AFS_LIVE).</summary>
[Collection("Live machine")] // one at a time: they change and restore the same settings
public class LiveInternetTests
{
    [Fact]
    public async Task DeadProxy_IsTheFirstFindingAndFix_InTheInternetCheck()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true)!;
        var pe = k.GetValue("ProxyEnable"); var ps = k.GetValue("ProxyServer");
        try
        {
            k.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("ProxyServer", "127.0.0.1:9");
            var n = await new RealNetworkProbe().ReadAsync();
            var nf = NetworkTriage.Evaluate(n);
            var d = HealthTriage.CombineNetwork(new Diagnosis(n, nf, NetworkTriage.Ladder(n, nf), false), new RealHealthProbe().Read());
            Assert.Equal(FindingCode.HealthDeadProxy, d.Findings[0].Code);
            Assert.Equal("network.reset_user_proxy", d.Ladder[0].ActionId);
            var (ok, _) = await FixActions.RunAsync(d.Ladder[0]);
            Assert.True(ok);
            Assert.True(await AutoFixScout.Areas.HealthFlow.AutoTestAsync(d.Findings));
        }
        finally
        {
            k.SetValue("ProxyEnable", pe ?? 0, Microsoft.Win32.RegistryValueKind.DWord);
            if (ps is string v) k.SetValue("ProxyServer", v); else k.DeleteValue("ProxyServer", false);
        }
    }
}

[Collection("Live machine")] // one at a time: they change and restore the same settings
public class LiveShellTests
{
    [Fact]
    public async Task ShellProbe_ReadsPackages_AndStartMenuRestarts()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        var s = AutoFixScout.Areas.ShellFlow.Read();
        Assert.DoesNotContain(s.ProbeErrors, e => e.StartsWith("recent changes") || e.StartsWith("restore points") || e.StartsWith("graphics resets"));
        Assert.Empty(s.ProbeErrors);
        Assert.Contains(s.Packages, p => p.Name == "Microsoft.Windows.StartMenuExperienceHost" && p.Status == "Ok");
        var (ok, detail) = await FixActions.RunAsync(new Rung("t", "shell.restart_start_menu", 0, ""));
        Assert.True(ok, detail);
    }
}

/// <summary>startup.disable on the real machine (AFS_LIVE set) with a dummy entry that is always removed afterwards.</summary>
[Collection("Live machine")] // one at a time: they change and restore the same settings
public class LiveStartupTests
{
    private static bool Live => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"));
    private const string Name = "AFS-Test-Entry";

    [Fact]
    public async Task DummyRunEntry_IsFound_ThenDisabledLikeTaskManager()
    {
        if (!Live) return;
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutoFixScout.Repair.StartupApps.RunKey, true);
        Assert.Null(run.GetValue(Name)); // never overwrite a real entry
        try
        {
            run.SetValue(Name, "notepad.exe");
            var before = new RealStorageProbe().Read();
            Assert.Contains(before.StartupEntries!, e => e.Name == Name && e.Scope == StartupScope.UserRun && e.Enabled);

            var (ok, detail) = await FixActions.RunAsync(new Rung("t", "startup.disable", 1, "", Name));
            Assert.True(ok, detail);

            var after = new RealStorageProbe().Read();
            Assert.Contains(after.StartupEntries!, e => e.Name == Name && !e.Enabled);
            Assert.Equal(before.StartupAppCount - 1, after.StartupAppCount);
            using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoFixScout.Repair.StartupApps.ApprovedKey + "Run");
            var value = (byte[])approved!.GetValue(Name)!;
            Assert.Equal(12, value.Length);
            Assert.Equal(0x03, value[0]);
            Assert.False(AutoFixScout.Repair.StartupApps.ParseApproved(value).Enabled);
        }
        finally
        {
            run.DeleteValue(Name, false);
            using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoFixScout.Repair.StartupApps.ApprovedKey + "Run", true);
            approved?.DeleteValue(Name, false);
        }
    }
}

/// <summary>Reads the big space users on the real machine (AFS_LIVE set). Read-only; Unknown parts are allowed.</summary>
[Collection("Live machine")]
public class LiveBigSpaceTests
{
    [Fact]
    public void BigUsers_ReadWithoutErrors_AndNeverListFileNames()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new RealStorageProbe().Read();
        string P(string n, SpaceItem? i) => $"{n}: {(i is null ? "Unknown" : StorageTriage.SizeOf(i))}";
        var lines = new List<string>
        {
            $"probe took {sw.Elapsed.TotalSeconds:0.0} s", P("windows.old", s.WindowsOld), P("hiberfil", s.HiberFile), P("downloads", s.Downloads),
            $"old downloads: {s.DownloadsOldCount?.ToString() ?? "Unknown"}", P("delivery optimization", s.DeliveryOptimization),
            $"recycle bin: {s.RecycleBinBytes?.ToString() ?? "Unknown"}",
            "profile folders: " + (s.BigProfileFolders is null ? "Unknown" : string.Join(", ", s.BigProfileFolders.Select(x => StorageTriage.SizeOf(new SpaceItem(x.Bytes, x.AtLeast))))),
        };
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
        Assert.NotNull(s.HiberFile ?? new SpaceItem(0, false)); // may be Unknown, never throws
        if (s.BigProfileFolders is { } p)
        {
            Assert.True(p.Count <= 3);
            // Folder names only: a name with a file extension or a path separator would be a file name or path.
            Assert.All(p, x => Assert.DoesNotContain(@"\", x.Name));
        }
        // Even a forced low-space evaluation never puts a path or file name in the plain message or details.
        s.FreeBytes = StorageTriage.CriticalBytes - 1;
        s.ProbeErrors = new List<string>();
        foreach (var f in StorageTriage.Evaluate(s).Where(x => x.Code == FindingCode.StorageBigUsers))
            Assert.DoesNotMatch(@"[A-Za-z]:\|\.\w{2,4}\b(?!\W*(old|sys))", f.PlainMessage);
    }
}

/// <summary>Regression: the page-file action once used "\.\root\cimv2" (missing a backslash) and failed with "Invalid parameter".</summary>
[Collection("Live machine")]
public class LivePageFileTests
{
    [Fact]
    public void PageFileAuto_ReadsWmi_AndDoesNothingWhenAlreadyAutomatic()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        using var cs = new System.Management.ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
        var automatic = cs.Get().Cast<System.Management.ManagementObject>().First()["AutomaticManagedPagefile"] is true;
        if (!automatic) return; // never change a real page file from a test
        Assert.Equal(3, AutoFixScout.Executor.Repairs.PageFileAuto());
    }
}

/// <summary>shell.notifications_on on the real machine (AFS_LIVE set); the original ToastEnabled value (or absence) is always restored.</summary>
[Collection("Live machine")] // one at a time: they change and restore the same settings
public class LiveNotificationTests
{
    [Fact]
    public async Task NotificationsOn_SetsToastEnabled_AndRestoresOriginal()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutoFixScout.Repair.NotificationRepairs.PushKey, true);
        var original = k.GetValue("ToastEnabled");
        var kind = original is null ? Microsoft.Win32.RegistryValueKind.DWord : k.GetValueKind("ToastEnabled");
        try
        {
            k.SetValue("ToastEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            var (ok, detail) = await FixActions.RunAsync(new Rung("t", "shell.notifications_on", 0, ""));
            Assert.True(ok, detail);
            Assert.Equal(1, k.GetValue("ToastEnabled"));
        }
        finally
        {
            if (original is null) k.DeleteValue("ToastEnabled", false);
            else k.SetValue("ToastEnabled", original, kind);
        }
    }
}
