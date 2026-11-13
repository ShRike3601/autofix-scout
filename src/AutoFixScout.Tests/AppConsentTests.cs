using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class AppConsentTests
{
    [Theory]
    [InlineData("C:#Users#Ann Lee#AppData#Local#Zoom#bin#Zoom.exe", "Zoom")]
    [InlineData("C:#AfsTest#afs-dummy.exe", "afs-dummy")]
    [InlineData("C:#Program Files#Mozilla Firefox#firefox.exe", "firefox")]
    [InlineData("C:#Tools#Skype.EXE", "Skype")]
    public void DesktopName_KeepsOnlyTheExeName(string key, string expected)
    {
        var name = AppConsent.DesktopName(key);
        Assert.Equal(expected, name);
        Assert.DoesNotContain("Ann", name);
        Assert.DoesNotContain("#", name);
    }

    [Theory]
    [InlineData("MicrosoftTeams_8wekyb3d8bbwe", "Microsoft Teams")]
    [InlineData("MSTeams_8wekyb3d8bbwe", "Microsoft Teams")]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Camera")]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "WhatsApp")]
    [InlineData("Microsoft.SkypeApp_kzf8qxf38zg5c", "Skype")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "SpotifyMusic")]
    public void PackagedName_MapsKnownFamilies(string key, string expected) => Assert.Equal(expected, AppConsent.PackagedName(key));

    [Fact]
    public void NameList_NamesAtMostThree()
    {
        Assert.Equal("\"A\"", AppConsent.NameList(new[] { "A" }));
        Assert.Equal("\"A\" and \"B\"", AppConsent.NameList(new[] { "A", "B" }));
        Assert.Equal("\"A\", \"B\" and \"C\"", AppConsent.NameList(new[] { "A", "B", "C", "D" }));
    }

    private static MicrophoneState Mic(params string[] denied) => new()
    {
        Services = new[] { new ServiceInfo("AudioSrv", "Running"), new ServiceInfo("AudioEndpointBuilder", "Running") },
        Endpoints = new[] { new MicEndpointInfo("Microphone (Realtek)", true, true, false, "Microphone") },
        DefaultMuted = Tri.No, DefaultLevelPercent = 80,
        DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes, DeniedApps = denied,
    };

    private static CameraState Cam(params string[] denied) => new()
    {
        Devices = new[] { new CameraDeviceInfo("Integrated Camera", @"USB\C\1", "Camera", 0) },
        DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes, DeniedApps = denied,
    };

    [Fact]
    public void Mic_AppDenied_NamedWithGuidedRung_NeverSilent()
    {
        var s = Mic("Zoom");
        var f = MicrophoneTriage.Evaluate(s);
        Assert.Equal(FindingCode.MicAppDenied, f[0].Code);
        Assert.Contains("Zoom", f[0].PlainMessage);
        var rung = MicrophoneTriage.Ladder(s, f).First(r => r.Id == "privacy_app");
        Assert.Equal("ui.open_settings_page", rung.ActionId);
        Assert.Equal("microphone", rung.Arg);
        Assert.Equal(0, rung.Tier);
        Assert.Contains("switch on access for \"Zoom\"", rung.Say);
    }

    [Fact]
    public void Mic_GlobalSwitchOff_ComesFirst_NoPerAppFinding()
    {
        var s = Mic("Zoom");
        s.DesktopAppAccess = Tri.No;
        var f = MicrophoneTriage.Evaluate(s);
        Assert.Equal(FindingCode.MicPrivacyOff, f[0].Code);
        Assert.DoesNotContain(f, x => x.Code == FindingCode.MicAppDenied);
    }

    [Fact]
    public void Mic_AppDeniedComesAfterMuted_AndRungIdsAreUnique()
    {
        var s = Mic("Zoom");
        s.DefaultMuted = Tri.Yes;
        var f = MicrophoneTriage.Evaluate(s);
        Assert.Equal(FindingCode.MicMuted, f[0].Code);
        Assert.Equal(FindingCode.MicAppDenied, f[1].Code);
        var ids = MicrophoneTriage.Ladder(s, f).Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Mic_NamesOnlyThree()
    {
        var f = MicrophoneTriage.Evaluate(Mic("A1", "B2", "C3", "D4"));
        Assert.DoesNotContain("D4", f[0].PlainMessage);
    }

    [Fact]
    public void Camera_AppDenied_NamedWithGuidedRung()
    {
        var s = Cam("Microsoft Teams", "Zoom");
        var f = CameraTriage.Evaluate(s);
        Assert.Equal(FindingCode.CameraAppDenied, f[0].Code);
        Assert.Contains("Microsoft Teams", f[0].PlainMessage);
        var rungs = CameraTriage.Ladder(s, f);
        Assert.Equal(rungs.Count, rungs.Select(r => r.Id).Distinct().Count());
        var rung = rungs.First(r => r.Id == "privacy_app");
        Assert.Equal("camera", rung.Arg);
        Assert.Contains("switch on access for \"Microsoft Teams\" and \"Zoom\"", rung.Say);
    }

    [Fact]
    public void Camera_GlobalPrivacyOffAndInUse_OutrankAppDenied()
    {
        var off = Cam("Zoom");
        off.AppAccess = Tri.No;
        Assert.Equal(FindingCode.CameraPrivacyOff, CameraTriage.Evaluate(off)[0].Code);
        var busy = Cam("Zoom");
        busy.AppsUsingCamera = new[] { "Teams" };
        Assert.Equal(FindingCode.CameraInUse, CameraTriage.Evaluate(busy)[0].Code);
    }

    [Fact]
    public void Camera_NoDeniedApps_StillLooksNormal() => Assert.Equal(FindingCode.LooksNormal, CameraTriage.Evaluate(Cam())[0].Code);

    [Fact]
    public void Sentences_FollowWritingGuide()
    {
        var s = Mic("Zoom", "Microsoft Teams");
        var f = MicrophoneTriage.Evaluate(s);
        var c = Cam("Zoom", "Camera");
        var cf = CameraTriage.Evaluate(c);
        var texts = f.Select(x => x.PlainMessage).Concat(MicrophoneTriage.Ladder(s, f).Select(r => r.Say))
            .Concat(cf.Select(x => x.PlainMessage)).Concat(CameraTriage.Ladder(c, cf).Select(r => r.Say)).ToList();
        var bad = new Regex(@"n't|'ll |'re |'ve |(^|[\s""(])I\s|\bwe\b", RegexOptions.IgnoreCase);
        foreach (var t in texts)
        {
            Assert.DoesNotContain("!", t);
            Assert.DoesNotMatch(bad, t);
        }
    }
}
