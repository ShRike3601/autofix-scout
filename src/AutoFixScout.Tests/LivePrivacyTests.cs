using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Microsoft.Win32;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>Creates a dummy desktop-app entry with Value=Deny in the microphone consent store, checks the finding, and always removes it.</summary>
[Collection("Live machine")]
public class LivePrivacyTests
{
    private const string Dummy = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#AfsTest#afs-dummy.exe";

    [Fact]
    public void DummyDeniedDesktopApp_IsNamed_AndRemoved()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var lines = new List<string>();
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Dummy)) k.SetValue("Value", "Deny", RegistryValueKind.String);
            var s = new RealMicrophoneProbe().Read();
            lines.Add("denied: " + string.Join(", ", s.DeniedApps));
            lines.Add($"switches {s.DeviceAccess}/{s.AppAccess}/{s.DesktopAppAccess}; errors {string.Join("|", s.ProbeErrors)}");
            Assert.Contains("afs-dummy", s.DeniedApps);
            if (s.DeviceAccess == Tri.Yes && s.AppAccess == Tri.Yes && s.DesktopAppAccess == Tri.Yes && s.ProbeErrors.Count == 0)
            {
                var f = MicrophoneTriage.Evaluate(s);
                lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}"));
                Assert.Contains(f, x => x.Code == FindingCode.MicAppDenied && x.PlainMessage.Contains("afs-dummy"));
            }
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(Dummy, false); } catch (Exception ex) { lines.Add("cleanup failed: " + ex.Message); }
            lines.Add("dummy still present: " + (Registry.CurrentUser.OpenSubKey(Dummy) != null));
            File.WriteAllLines(output, lines);
        }
        Assert.Null(Registry.CurrentUser.OpenSubKey(Dummy));
    }
}
