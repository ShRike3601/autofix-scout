using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class MicrophoneTests
{
    private static readonly ServiceInfo[] Running = { new("AudioSrv", "Running"), new("AudioEndpointBuilder", "Running") };
    private static readonly MicEndpointInfo Mic = new("Microphone (Realtek)", true, true, false, "Microphone");

    private static MicrophoneState Healthy() => new()
    {
        Services = Running, Endpoints = new[] { Mic }, DefaultMuted = Tri.No, DefaultLevelPercent = 80,
        DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes,
    };

    private static (FindingCode Code, string[] Ladder) Run(MicrophoneState s)
    {
        var f = MicrophoneTriage.Evaluate(s);
        return (f[0].Code, MicrophoneTriage.Ladder(s, f).Select(r => r.ActionId).ToArray());
    }

    [Fact]
    public void Working_LooksNormal() => Assert.Equal(FindingCode.LooksNormal, Run(Healthy()).Code);

    [Fact]
    public void Muted_UnmuteIsOffered_WithoutAdmin()
    {
        var s = Healthy();
        s.DefaultMuted = Tri.Yes;
        var (code, ladder) = Run(s);
        Assert.Equal(FindingCode.MicMuted, code);
        Assert.Equal("audio.unmute_microphone", ladder[0]);
    }

    [Fact]
    public void PrivacyOff_OpensMicrophonePrivacySettings()
    {
        var s = Healthy();
        s.DesktopAppAccess = Tri.No;
        var f = MicrophoneTriage.Evaluate(s);
        Assert.Equal(FindingCode.MicPrivacyOff, f[0].Code);
        Assert.Contains(MicrophoneTriage.Ladder(s, f), r => r.ActionId == "ui.open_settings_page" && r.Arg == "microphone");
    }

    [Fact]
    public void NoMicrophone_NeverChangesDrivers()
    {
        var s = Healthy();
        s.Endpoints = Array.Empty<MicEndpointInfo>();
        var (code, ladder) = Run(s);
        Assert.Equal(FindingCode.NoMicrophone, code);
        Assert.DoesNotContain(ladder, a => a.StartsWith("driver."));
    }

    [Fact]
    public void ProbeError_FailsClosed()
    {
        var (code, ladder) = Run(new MicrophoneState { ProbeErrors = new[] { "Core Audio" } });
        Assert.Equal(FindingCode.ProbeIncomplete, code);
        Assert.Empty(ladder);
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var states = new List<MicrophoneState> { Healthy() };
        var muted = Healthy(); muted.DefaultMuted = Tri.Yes; states.Add(muted);
        var priv = Healthy(); priv.DeviceAccess = Tri.No; states.Add(priv);
        var none = Healthy(); none.Endpoints = Array.Empty<MicEndpointInfo>(); states.Add(none);
        foreach (var s in states)
        {
            var f = MicrophoneTriage.Evaluate(s);
            foreach (var t in f.Select(x => x.PlainMessage).Concat(MicrophoneTriage.Ladder(s, f).Select(r => r.Say)))
                Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|!|\bit's\b|\bthat's\b", t);
        }
    }
}
