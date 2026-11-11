using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class CameraTriageTests
{
    private static readonly CameraDeviceInfo Cam = new("Integrated Camera", @"USB\VID_04F2&PID_B6D9&MI_00\6&1", "Camera", 0);

    private static CameraState Ok(params CameraDeviceInfo[] devices) => new()
    {
        Devices = devices.Length == 0 ? new[] { Cam } : devices,
        DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes,
    };

    private static FindingCode Code(CameraState s) => CameraTriage.Evaluate(s)[0].Code;
    private static string[] Ladder(CameraState s) => CameraTriage.Ladder(s, CameraTriage.Evaluate(s)).Select(r => r.Id).ToArray();

    [Fact]
    public void Healthy_LooksNormal_CloseAppsThenShutterThenToggle()
    {
        var s = Ok();
        Assert.Equal(FindingCode.LooksNormal, Code(s));
        // Windows 26300 doesn't report live camera use (test step 4d), so "close other apps" comes first.
        Assert.Equal(new[] { "close_other_apps", "shutter", "toggle_camera" }, Ladder(s));
    }

    [Fact]
    public void UnreadPrivacy_FailsClosed_NoFixes()
    {
        var s = Ok();
        s.DesktopAppAccess = Tri.Unknown;
        Assert.Equal(FindingCode.ProbeIncomplete, Code(s));
        Assert.Empty(Ladder(s));
    }

    [Fact]
    public void NoCamera_ShutterOnly_ProbablyHardware()
    {
        var s = new CameraState { DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes };
        Assert.Equal(FindingCode.NoCamera, Code(s));
        Assert.Equal(new[] { "shutter" }, Ladder(s));
        Assert.True(CameraTriage.ProbablyHardware(s));
    }

    [Fact]
    public void Disabled_EnableFirst()
    {
        var s = Ok(Cam with { ProblemCode = 22 });
        Assert.Equal(FindingCode.CameraDisabled, Code(s));
        Assert.Equal("enable_camera", Ladder(s)[0]);
        Assert.False(CameraTriage.ProbablyHardware(s));
    }

    [Fact]
    public void Error_TogglesBrokenDevice()
    {
        var s = Ok(Cam with { ProblemCode = 10 });
        Assert.Equal(FindingCode.CameraError, Code(s));
        Assert.Equal("toggle_camera", Ladder(s)[0]);
    }

    [Fact]
    public void DesktopAppsBlocked_NamesTheSwitch()
    {
        var s = Ok();
        s.DesktopAppAccess = Tri.No;
        Assert.Equal(FindingCode.CameraPrivacyOff, Code(s));
        var rung = CameraTriage.Ladder(s, CameraTriage.Evaluate(s))[0];
        Assert.Contains("\"Let desktop apps access your camera\"", rung.Say);
        Assert.DoesNotContain("\"Camera access\"", rung.Say);
    }

    [Fact]
    public void InUse_NamesTheApp()
    {
        var s = Ok();
        s.AppsUsingCamera = new[] { "Zoom" };
        Assert.Equal(FindingCode.CameraInUse, Code(s));
        Assert.Contains("Zoom", CameraTriage.Ladder(s, CameraTriage.Evaluate(s))[0].Say);
    }

    [Fact]
    public void PrefersCameraClassOverImageClass_ForToggle()
    {
        var scanner = new CameraDeviceInfo("HP Scanner", @"USB\SCANNER\1", "Image", 0);
        var toggle = CameraTriage.Ladder(Ok(scanner, Cam), CameraTriage.Evaluate(Ok(scanner, Cam))).Single(r => r.Id == "toggle_camera");
        Assert.Equal(Cam.InstanceId, toggle.Arg);
    }

    [Theory]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", true, "WindowsCamera")]
    [InlineData("C:#Program Files#Zoom#bin#Zoom.exe", false, "Zoom")]
    public void FriendlyAppName(string key, bool packaged, string expected) =>
        Assert.Equal(expected, RealCameraProbe.FriendlyAppName(key, packaged));
}
