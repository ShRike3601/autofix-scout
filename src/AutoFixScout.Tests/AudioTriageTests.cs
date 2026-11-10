using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class AudioTriageTests
{
    private static readonly ServiceInfo[] Running = { new("AudioSrv", "Running"), new("AudioEndpointBuilder", "Running") };
    private static readonly AudioEndpointInfo Speakers = new("Speakers (Realtek)", true, true, "Speakers");

    private static FindingCode Top(AudioState s) => AudioTriage.Evaluate(new FakeAudioProbe(s).Read())[0].Code;

    [Fact]
    public void Healthy_LooksNormal() =>
        Assert.Equal(FindingCode.LooksNormal, Top(new() { Services = Running, Endpoints = new[] { Speakers }, DefaultMuted = Tri.No, DefaultVolumePercent = 50 }));

    [Fact]
    public void Muted_IsReported() =>
        Assert.Equal(FindingCode.Muted, Top(new() { Services = Running, Endpoints = new[] { Speakers }, DefaultMuted = Tri.Yes, DefaultVolumePercent = 50 }));

    [Fact]
    public void ProbeError_FailsClosed() =>
        Assert.Equal(FindingCode.ProbeIncomplete, Top(new() { ProbeErrors = new[] { "WMI down" } }));

    [Fact]
    public void UnknownMute_NeverLooksNormal() =>
        Assert.DoesNotContain(AudioTriage.Evaluate(new AudioState { Services = Running, Endpoints = new[] { Speakers } }), f => f.Code == FindingCode.LooksNormal);

    [Fact]
    public void DefaultOnHdmi_WithWorkingSpeakers_IsReported()
    {
        var hdmi = new AudioEndpointInfo("LG TV", true, true, "Hdmi");
        var speakers = Speakers with { IsDefault = false };
        var findings = AudioTriage.Evaluate(new AudioState { Services = Running, Endpoints = new[] { hdmi, speakers }, DefaultMuted = Tri.No, DefaultVolumePercent = 50 });
        Assert.Contains(findings, f => f.Code == FindingCode.DefaultIsHdmiOrBluetooth);
    }

    [Theory]
    [InlineData("Headphones (WH-1000XM4 Hands-Free)", null, "Bluetooth")]
    [InlineData("Speakers (Realtek(R) Audio)", 1, "Speakers")]
    [InlineData("Realtek Audio", 3, "Headphones")]
    public void GuessKind_FromNameAndFormFactor(string name, int? formFactor, string expected) =>
        Assert.Equal(expected, RealAudioProbe.GuessKind(name, formFactor));
}
