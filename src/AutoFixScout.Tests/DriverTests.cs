using System.Text;
using AutoFixScout.Drivers;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class DriverStoreTests : IDisposable
{
    private const string Hwid = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0236&SUBSYS_17AA2233";
    private readonly string _dir = Directory.CreateTempSubdirectory("afs-inf-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Real INFs are usually UTF-16 with a BOM; write some that way to cover the decoding.
    private void Inf(string name, string cls, string version, string hwid, bool utf16 = true) =>
        File.WriteAllText(Path.Combine(_dir, name),
            $"; comment\r\n[Version]\r\nSignature=\"$Windows NT$\"\r\nClass={cls} ; trailing comment\r\nDriverVer = 03/01/2024,{version}\r\n\r\n" +
            $"[Models]\r\n%Dev% = Install, {hwid}\r\n",
            utf16 ? Encoding.Unicode : Encoding.ASCII);

    private DeviceDriver Device(string inf = "oem10.inf", string version = "6.0.9600.1") =>
        new("HDAUDIO\\X\\1", new[] { Hwid, @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0236" }, 0, inf, version);

    [Fact]
    public void FindsNewestOlderPackageForSameDevice()
    {
        Inf("oem10.inf", "MEDIA", "6.0.9600.1", Hwid);             // current
        Inf("oem3.inf", "MEDIA", "6.0.9100.1", Hwid);              // older
        Inf("oem7.inf", "MEDIA", "6.0.9400.1", Hwid, utf16: false); // newest older -> pick this
        Inf("oem8.inf", "MEDIA", "6.0.9700.1", Hwid);              // newer: never a rollback
        Inf("oem9.inf", "Net", "1.0.0.0", Hwid);                   // other class
        Inf("oem4.inf", "MEDIA", "5.0.0.0", @"HDAUDIO\FUNC_01&VEN_8086&DEV_2812"); // other device

        var prev = DriverStore.FindPrevious(Device(), _dir);
        Assert.NotNull(prev);
        Assert.Equal("oem7.inf", Path.GetFileName(prev!.InfPath));
        Assert.Equal(new Version(6, 0, 9400, 1), prev.Version);
        Assert.Equal(Hwid, prev.MatchedHardwareId); // most specific id, so the install targets this device
    }

    [Fact]
    public void NothingOlder_ReturnsNull()
    {
        Inf("oem10.inf", "MEDIA", "6.0.9600.1", Hwid);
        Inf("oem8.inf", "MEDIA", "6.0.9700.1", Hwid);
        Assert.Null(DriverStore.FindPrevious(Device(), _dir));
    }

    [Fact]
    public void UnreadableCurrentDriver_ReturnsNull()
    {
        Inf("oem3.inf", "MEDIA", "6.0.9100.1", Hwid);
        Assert.Null(DriverStore.FindPrevious(Device(inf: "oem99.inf"), _dir));      // current INF missing
        Assert.Null(DriverStore.FindPrevious(Device(version: "unknown"), _dir));    // version unparseable
    }

    [Fact]
    public void ReadsVersionSectionOnly()
    {
        File.WriteAllText(Path.Combine(_dir, "oem1.inf"), "[Version]\nClass=Camera\nDriverVer=01/01/2020,10.0.1.2\n[Other]\nClass=MEDIA\nDriverVer=01/01/2020,99.0\n");
        Assert.Equal(("Camera", new Version(10, 0, 1, 2)), DriverStore.ReadVersionSection(Path.Combine(_dir, "oem1.inf")));
    }
}

public class DriverLadderTests
{
    private static DriverStatus Recent(string? previous = "6.0.9400.1", int daysAgo = 3) =>
        new("6.0.9600.1", DateTime.UtcNow.AddDays(-daysAgo), previous);

    [Fact]
    public void Rollback_OnlyWhenRecentAndPreviousExists()
    {
        Assert.NotNull(DriverRules.Rollback("sound", "Realtek", "X", Recent()));
        Assert.Null(DriverRules.Rollback("sound", "Realtek", "X", Recent(previous: null)));
        Assert.Null(DriverRules.Rollback("sound", "Realtek", "X", Recent(daysAgo: 45)));
        Assert.Null(DriverRules.Rollback("sound", "Realtek", "X", null));
    }

    [Fact]
    public void Audio_RollbackIsLastRung_AndTier2()
    {
        var s = new AudioState
        {
            Services = new[] { new ServiceInfo("AudioSrv", "Running"), new ServiceInfo("AudioEndpointBuilder", "Running") },
            Endpoints = new[] { new AudioEndpointInfo("Speakers", true, true, "Speakers") },
            DefaultMuted = Tri.No, DefaultVolumePercent = 50,
            Devices = new[] { new AudioDeviceInfo("Realtek Audio", "HDAUDIO\\X\\1", 0, null, null, null, Recent()) },
        };
        var last = AudioLadder.For(s, AudioTriage.Evaluate(s)).Last();
        Assert.Equal(("rollback_driver", "driver.rollback", 2, "HDAUDIO\\X\\1"), (last.Id, last.ActionId, last.Tier, last.Arg));
        Assert.Contains("save point", last.Say);
    }

    [Fact]
    public void Camera_RollbackBeforeFinalShutterHint()
    {
        var s = new CameraState
        {
            Devices = new[] { new CameraDeviceInfo("Integrated Camera", "USB\\C\\1", "Camera", 0, Recent()) },
            DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes,
        };
        Assert.Equal(new[] { "shutter", "toggle_camera", "rollback_driver" },
            CameraTriage.Ladder(s, CameraTriage.Evaluate(s)).Select(r => r.Id));
    }

    [Fact]
    public void Network_RollbackBeforeRouterStep()
    {
        var s = new NetworkState
        {
            Adapters = new[] { new NetAdapterInfo("Intel Wi-Fi", "PCI\\W\\1", true, true, 0, Recent()) },
            AirplaneMode = Tri.No, GatewayReachable = Tri.Yes, DnsResolving = Tri.No, InternetReachable = Tri.No,
        };
        Assert.Equal(new[] { "flush_dns", "toggle_adapter", "rollback_driver", "restart_router" },
            NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s)).Select(r => r.Id));
    }

    [Fact]
    public void WindowsUpdate_OnlyForDevicesStillReportingAnError()
    {
        Assert.NotNull(DriverRules.WindowsUpdate("sound", "Realtek", "X", 10));
        Assert.Null(DriverRules.WindowsUpdate("sound", "Realtek", "X", 0));   // working device: never touched
        Assert.Null(DriverRules.WindowsUpdate("sound", "Realtek", "X", 22));  // just disabled: enabling is the fix
        Assert.Null(DriverRules.WindowsUpdate("sound", "Realtek", "X", null)); // unknown: fail closed
    }

    [Fact]
    public void Audio_BrokenDevice_ToggleThenRollbackThenWindowsUpdate()
    {
        var s = new AudioState
        {
            Services = new[] { new ServiceInfo("AudioSrv", "Running"), new ServiceInfo("AudioEndpointBuilder", "Running") },
            Endpoints = new[] { new AudioEndpointInfo("Speakers", true, true, "Speakers") },
            DefaultMuted = Tri.No, DefaultVolumePercent = 50,
            Devices = new[] { new AudioDeviceInfo("Realtek Audio", @"HDAUDIO\X\1", 10, null, null, null, Recent()) },
        };
        var ladder = AudioLadder.For(s, AudioTriage.Evaluate(s));
        Assert.Equal(new[] { "restart_services", "toggle_device", "rollback_driver", "windows_update_driver" }, ladder.Select(r => r.Id));
        Assert.All(ladder.Where(r => r.Tier == 2), r => Assert.Contains("save point", r.Say));
    }

    [Fact]
    public void Camera_BrokenDevice_WindowsUpdateBeforeShutter()
    {
        var s = new CameraState
        {
            Devices = new[] { new CameraDeviceInfo("Integrated Camera", @"USB\C\1", "Camera", 10) },
            DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes,
        };
        Assert.Equal(new[] { "toggle_camera", "windows_update_driver", "shutter" },
            CameraTriage.Ladder(s, CameraTriage.Evaluate(s)).Select(r => r.Id));
    }

    // ---- runner: save point settle / undo ----

    private sealed class T2Host(bool runOk, bool fixedIt, bool settleOk) : IFixHost
    {
        public List<(string Rung, bool Keep)> Settled { get; } = new();
        public int Ran;
        public Task<bool> ConfirmAsync(Rung rung) => Task.FromResult(true);
        public Task<(bool Ok, string Detail)> RunAsync(Rung rung) { Ran++; return Task.FromResult((runOk, "")); }
        public Task<bool> TestAsync() => Task.FromResult(fixedIt);
        public Task<bool> SettleAsync(Rung rung, bool keep) { Settled.Add((rung.Id, keep)); return Task.FromResult(settleOk); }
    }

    private static readonly Rung T2 = new("rollback_driver", "driver.rollback", 2, "x", "id");
    private static readonly Rung After = new("restart_router", "ui.human_step", 0, "y");

    [Fact]
    public async Task Fixed_KeepsSavePoint()
    {
        var host = new T2Host(true, true, true);
        var steps = new List<StepLog>();
        Assert.Equal(Outcome.Fixed, await LadderRunner.RunAsync(new[] { T2 }, host, steps));
        Assert.Equal(new[] { ("rollback_driver", true) }, host.Settled);
        Assert.Equal(StepResult.Success, steps[0].Result);
    }

    [Fact]
    public async Task NotFixed_Undoes_AndContinues()
    {
        var host = new T2Host(true, false, true);
        var steps = new List<StepLog>();
        await LadderRunner.RunAsync(new[] { T2, After }, host, steps);
        Assert.Equal(new[] { ("rollback_driver", false) }, host.Settled);
        Assert.Equal(StepResult.Undone, steps[0].Result);
        Assert.Equal(2, host.Ran);
    }

    [Fact]
    public async Task UndoFails_StopsEverything()
    {
        var host = new T2Host(true, false, settleOk: false);
        var steps = new List<StepLog>();
        Assert.Equal(Outcome.NotFixed, await LadderRunner.RunAsync(new[] { T2, After }, host, steps));
        Assert.Equal(1, host.Ran); // nothing after an unrecovered change
        Assert.Equal(StepResult.Failed, steps[0].Result);
        Assert.Contains("undo failed", steps[0].Error);
    }

    [Fact]
    public async Task FailedT2Run_IsSettled_ThenStopsIfRestoreFails()
    {
        var host = new T2Host(runOk: false, fixedIt: false, settleOk: false);
        Assert.Equal(Outcome.NotFixed, await LadderRunner.RunAsync(new[] { T2, After }, host, new()));
        Assert.Equal(new[] { ("rollback_driver", false) }, host.Settled);
        Assert.Equal(1, host.Ran);
    }

    [Fact]
    public async Task AtMostThreeT2PerSession()
    {
        var host = new T2Host(true, false, true);
        var ladder = Enumerable.Range(0, 5).Select(i => T2 with { Id = $"t2_{i}" }).ToArray();
        var steps = new List<StepLog>();
        await LadderRunner.RunAsync(ladder, host, steps);
        Assert.Equal(LadderRunner.MaxT2PerSession, host.Ran);
        Assert.Equal(2, steps.Count(s => s.Result == StepResult.Skipped));
    }

    // ---- executor input validation ----

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("..\\..\\Windows", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("", false)]
    public void SavePointIdValidation(string id, bool ok) => Assert.Equal(ok, ExecutorProgram.IsValidSavePointId(id));

    [Fact]
    public void Executor_RejectsBadDriverArgs()
    {
        Assert.Equal(2, ExecutorProgram.Main(new[] { "driver.restore", "..\\evil" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "driver.rollback", "a b", "0123456789abcdef0123456789abcdef" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "driver.rollback", "only-two-args" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "driver.install_from_windows_update", "x'; --", "0123456789abcdef0123456789abcdef" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "driver.install_from_windows_update", @"HDAUDIO\X", "not-a-guid" }));
    }
}
