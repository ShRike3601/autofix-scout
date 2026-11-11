using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

/// <summary>"Scan for hardware changes" for a device that was uninstalled or dropped off (friends' test plan).</summary>
public class RescanTests
{
    private static readonly ServiceInfo[] Running = { new("AudioSrv", "Running"), new("AudioEndpointBuilder", "Running") };

    [Fact]
    public void Sound_NoOutputAtAll_RescanAfterServiceRestart()
    {
        // Realtek uninstalled: no endpoints left (HDMI audio devices may still be listed, with nothing plugged in).
        var s = new AudioState { Services = Running, DefaultMuted = Tri.Unknown };
        var ladder = AudioLadder.For(s, AudioTriage.Evaluate(s));
        Assert.Equal(new[] { "restart_services", "rescan" }, ladder.Select(r => r.Id));
        Assert.Equal(("device.rescan", "audio", 1), (ladder[1].ActionId, ladder[1].Arg, ladder[1].Tier));
    }

    [Fact]
    public void Sound_WorkingSpeakers_NoRescan()
    {
        var s = new AudioState { Services = Running, Endpoints = new[] { new AudioEndpointInfo("Speakers", true, true, "Speakers") },
            DefaultMuted = Tri.Yes, DefaultVolumePercent = 50 };
        Assert.DoesNotContain(AudioLadder.For(s, AudioTriage.Evaluate(s)), r => r.Id == "rescan");
    }

    [Fact]
    public void Network_NoAdapter_Rescan()
    {
        var s = new NetworkState { AirplaneMode = Tri.No };
        var ladder = NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s));
        Assert.Equal(("rescan", "network"), (ladder.Single().Id, ladder.Single().Arg));
    }

    [Fact]
    public void Executor_AllowsOnlyKnownAreas_SameListAsTheApp()
    {
        Assert.Equal(Rescan.Areas, ExecutorProgram.RescanAreas);
        Assert.Equal(2, ExecutorProgram.Main(new[] { "device.rescan", "printer" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "device.rescan" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "device.rescan", "audio", "x" }));
    }
}
