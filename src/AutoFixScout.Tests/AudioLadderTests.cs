using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class AudioLadderTests
{
    private static readonly ServiceInfo[] Running = { new("AudioSrv", "Running"), new("AudioEndpointBuilder", "Running") };
    private static readonly AudioEndpointInfo Speakers = new("Speakers (Realtek)", true, true, "Speakers");

    private static string[] Ladder(AudioState s) => AudioLadder.For(s, AudioTriage.Evaluate(s)).Select(r => r.Id).ToArray();

    [Fact]
    public void Muted_UnmuteFirst_ThenRestart() =>
        Assert.Equal(new[] { "unmute", "restart_services" },
            Ladder(new() { Services = Running, Endpoints = new[] { Speakers }, DefaultMuted = Tri.Yes, DefaultVolumePercent = 50 }));

    [Fact]
    public void HdmiDefault_WrongOutputFirst()
    {
        var hdmi = new AudioEndpointInfo("LG TV", true, true, "Hdmi");
        var ladder = Ladder(new() { Services = Running, Endpoints = new[] { hdmi, Speakers with { IsDefault = false } }, DefaultMuted = Tri.No, DefaultVolumePercent = 50 });
        Assert.Equal("wrong_output", ladder[0]);
    }

    [Fact]
    public void DeviceError_AddsToggleForThatDevice()
    {
        var s = new AudioState { Services = Running, Endpoints = new[] { Speakers }, DefaultMuted = Tri.No, DefaultVolumePercent = 50,
            Devices = new[] { new AudioDeviceInfo("Realtek Audio", @"HDAUDIO\FUNC_01&VEN_10EC\4&1", 10, null, null, null) } };
        var toggle = AudioLadder.For(s, AudioTriage.Evaluate(s)).Single(r => r.Id == "toggle_device");
        Assert.Equal(@"HDAUDIO\FUNC_01&VEN_10EC\4&1", toggle.Arg);
    }

    [Fact]
    public void ProbeIncomplete_NoFixesOffered() =>
        Assert.Empty(Ladder(new() { ProbeErrors = new[] { "WMI down" } }));

    [Fact]
    public void ProbablyHardware_NoActiveOutputAndDeviceError()
    {
        var s = new AudioState { Devices = new[] { new AudioDeviceInfo("Realtek Audio", "X", 43, null, null, null) } };
        Assert.True(AudioLadder.ProbablyHardware(s));
        Assert.False(AudioLadder.ProbablyHardware(new AudioState { Endpoints = new[] { Speakers }, Devices = s.Devices }));
    }

    // ---- runner ----

    private sealed class FakeHost(bool confirm, bool runOk, params bool[] heard) : IFixHost
    {
        private int _tests;
        public List<string> Ran { get; } = new();
        public Task<bool> ConfirmAsync(Rung rung) => Task.FromResult(confirm);
        public Task<(bool, string)> RunAsync(Rung rung) { Ran.Add(rung.Id); return Task.FromResult((runOk, "")); }
        public Task<bool> TestAsync() => Task.FromResult(heard[_tests++]);
        public Task<bool> SettleAsync(Rung rung, bool keep) => Task.FromResult(true);
    }

    private static readonly Rung[] TwoRungs = { new("a", "x", 1, "a"), new("b", "y", 1, "b") };

    [Fact]
    public async Task Runner_StopsAtFirstFix()
    {
        var host = new FakeHost(true, true, false, true);
        Assert.Equal(Outcome.Fixed, await LadderRunner.RunAsync(TwoRungs, host, new()));
        Assert.Equal(new[] { "a", "b" }, host.Ran);
    }

    [Fact]
    public async Task Runner_NotNow_Abandons_AndRunsNothing()
    {
        var host = new FakeHost(false, true);
        var log = new List<StepLog>();
        Assert.Equal(Outcome.Abandoned, await LadderRunner.RunAsync(TwoRungs, host, log));
        Assert.Empty(host.Ran);
        Assert.Equal((false, StepResult.Skipped), (log.Single().Approved, log.Single().Result));
    }

    [Fact]
    public async Task Runner_FailedRung_SkipsTest_AndContinues()
    {
        var log = new List<StepLog>();
        Assert.Equal(Outcome.NotFixed, await LadderRunner.RunAsync(TwoRungs, new FakeHost(true, false), log));
        Assert.All(log, s => Assert.Equal((true, StepResult.Failed, (bool?)null), (s.Approved, s.Result, s.UserConfirmedFixed)));
    }

    // ---- test before fixing when nothing looks wrong (found in real-hardware test step 1) ----

    private static readonly Triage.Finding Normal = new(FindingCode.LooksNormal, "fine", "");

    [Fact]
    public async Task NoFault_LooksNormalAndTestWorks_NoFaultFound()
    {
        var tests = 0;
        Assert.Equal(Outcome.NoFaultFound, await LadderRunner.NoFaultAsync(new[] { Normal }, () => { tests++; return Task.FromResult(true); }));
        Assert.Equal(1, tests);
    }

    [Fact]
    public async Task NoFault_TestFails_GoesOnToLadder() =>
        Assert.Null(await LadderRunner.NoFaultAsync(new[] { Normal }, () => Task.FromResult(false)));

    [Fact]
    public async Task NoFault_RealFinding_DoesNotRunTest()
    {
        var tested = false;
        var findings = new[] { new Triage.Finding(FindingCode.Muted, "muted", ""), Normal };
        Assert.Null(await LadderRunner.NoFaultAsync(findings, () => { tested = true; return Task.FromResult(true); }));
        Assert.False(tested);
    }

    // ---- executor input validation (trust boundary) ----

    [Theory]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0236\4&2B6A5E6A&0&0001", true)]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{a1b2}", true)]
    [InlineData("x\" /add-driver evil.inf", false)]
    [InlineData("a b", false)]
    [InlineData("x'; --", false)]
    [InlineData("", false)]
    public void Executor_InstanceIdValidation(string id, bool ok) => Assert.Equal(ok, ExecutorProgram.IsValidInstanceId(id));

    [Fact]
    public void Executor_RejectsUnknownActionsAndServices()
    {
        Assert.Equal(2, ExecutorProgram.Main(new[] { "cmd.exe", "/c whoami" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "service.restart", "WinDefend" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "service.restart" }));
    }
}
