using System.Text.RegularExpressions;
using AutoFixScout.Drivers;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>
/// docs/WRITING.md: every sentence the triage classes show the person is formal. No contractions, the app never
/// speaks as "I", no exclamation marks. Checked over representative states so every finding and rung is produced.
/// </summary>
public class WritingStyleTests
{
    private static readonly string[] Contractions = { "n't", "'d ", "'ll ", "I'm", "it's", "that's", "'re ", "'ve " };

    private static DriverStatus Recent() => new("6.0.9600.1", DateTime.UtcNow.AddDays(-3), "6.0.9400.1");

    private static readonly ServiceInfo[] Running =
        { new("AudioSrv", "Running"), new("AudioEndpointBuilder", "Running") };

    private static IEnumerable<AudioState> AudioStates()
    {
        var speakers = new AudioEndpointInfo("Speakers (Realtek Audio)", false, true, "Speakers", @"HDAUDIO\X\1");
        var hdmi = new AudioEndpointInfo("LG TV (HDMI)", true, true, "Hdmi");
        var broken = new AudioDeviceInfo("Realtek Audio", @"HDAUDIO\X\1", 10, null, null, null, Recent());
        yield return new AudioState { ProbeErrors = new[] { "WMI" } };
        yield return new AudioState
        {
            Services = new[] { new ServiceInfo("AudioSrv", "Stopped"), new ServiceInfo("AudioEndpointBuilder", "Stopped") },
            Devices = new[] { broken }, DefaultMuted = Tri.Yes, DefaultVolumePercent = 2,
        };
        yield return new AudioState
        {
            Services = Running, Endpoints = new[] { speakers with { IsActive = false } },
            Devices = new[] { broken }, DefaultMuted = Tri.No, DefaultVolumePercent = 50,
        };
        yield return new AudioState
        {
            Services = Running, Endpoints = new[] { hdmi, speakers },
            Devices = new[] { broken with { ProblemCode = 0 } }, DefaultMuted = Tri.Yes, DefaultVolumePercent = 3,
        };
        yield return new AudioState
        {
            Services = Running, Endpoints = new[] { speakers with { IsDefault = true } },
            Devices = new[] { broken with { ProblemCode = 0 } }, DefaultMuted = Tri.No, DefaultVolumePercent = 50,
        };
    }

    private static IEnumerable<CameraState> CameraStates()
    {
        var cam = new CameraDeviceInfo("Integrated Camera", @"USB\C\1", "Camera", 0, Recent());
        CameraState Ok(params CameraDeviceInfo[] d) =>
            new() { Devices = d, DeviceAccess = Tri.Yes, AppAccess = Tri.Yes, DesktopAppAccess = Tri.Yes };
        yield return new CameraState { ProbeErrors = new[] { "WMI" } };
        yield return Ok();
        yield return Ok(cam with { ProblemCode = 22 });
        yield return Ok(cam with { ProblemCode = 10 });
        var privacy = Ok(cam);
        privacy.AppAccess = Tri.No;
        privacy.DesktopAppAccess = Tri.No;
        yield return privacy;
        var inUse = Ok(cam);
        inUse.AppsUsingCamera = new[] { "Zoom" };
        yield return inUse;
        var inUseTwice = Ok(cam);
        inUseTwice.AppsUsingCamera = new[] { "Zoom", "Teams" };
        yield return inUseTwice;
        var unread = Ok(cam);
        unread.DeviceAccess = Tri.Unknown;
        yield return unread;
        yield return Ok(cam);
    }

    private static IEnumerable<NetworkState> NetworkStates()
    {
        var wifi = new NetAdapterInfo("Intel Wi-Fi 6 AX201", @"PCI\W\1", true, true, 0, Recent());
        var down = wifi with { Connected = false };
        NetworkState Online(Tri gw, Tri dns, Tri net) =>
            new() { Adapters = new[] { wifi }, AirplaneMode = Tri.No, GatewayReachable = gw, DnsResolving = dns, InternetReachable = net };
        yield return new NetworkState { ProbeErrors = new[] { "WMI" } };
        yield return new NetworkState();
        yield return new NetworkState { Adapters = new[] { down }, AirplaneMode = Tri.Yes };
        yield return new NetworkState { Adapters = new[] { down with { ProblemCode = 22 } }, AirplaneMode = Tri.No };
        yield return new NetworkState { Adapters = new[] { down with { ProblemCode = 10 } }, AirplaneMode = Tri.No };
        yield return new NetworkState { Adapters = new[] { down }, AirplaneMode = Tri.No };
        yield return new NetworkState { Adapters = new[] { new NetAdapterInfo("Realtek PCIe GbE", @"PCI\E\1", false, false, 0) }, AirplaneMode = Tri.No };
        yield return Online(Tri.Yes, Tri.Yes, Tri.Yes);
        yield return Online(Tri.No, Tri.No, Tri.No);
        yield return Online(Tri.Yes, Tri.Yes, Tri.No);
        yield return Online(Tri.Unknown, Tri.Unknown, Tri.Unknown);
        var dns = Online(Tri.Yes, Tri.No, Tri.No);
        dns.StaticDnsServers = new[] { "192.0.2.1" };
        dns.StaticDnsAdapterId = wifi.InstanceId;
        yield return dns;
        var extras = Online(Tri.Yes, Tri.Yes, Tri.Yes);
        extras.AddressObtained = Tri.No; extras.WifiSignalPercent = 20; extras.Supports5GHz = Tri.Yes; extras.IpConflictRecent = true; extras.Metered = Tri.Yes;
        yield return extras;
        var noAddress = Online(Tri.No, Tri.No, Tri.No);
        noAddress.AddressObtained = Tri.No;
        yield return noAddress;
        var weak = Online(Tri.Yes, Tri.Yes, Tri.Yes);
        weak.WifiSignalPercent = 30;
        yield return weak;
    }

    /// <summary>Every finding message, rung sentence and "not fixed" hint the triage classes produce for the states above.</summary>
    public static IEnumerable<object[]> Sentences()
    {
        var texts = new List<string>();
        foreach (var s in AudioStates())
        {
            var f = AudioTriage.Evaluate(s);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(AudioLadder.For(s, f).Select(r => r.Say));
        }
        foreach (var s in CameraStates())
        {
            var f = CameraTriage.Evaluate(s);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(CameraTriage.Ladder(s, f).Select(r => r.Say));
        }
        foreach (var s in NetworkStates())
        {
            var f = NetworkTriage.Evaluate(s);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(NetworkTriage.Ladder(s, f).Select(r => r.Say));
            if (NetworkTriage.NotFixedHint(f) is { } hint) texts.Add(hint);
        }
        foreach (var s in SlowComputerTests.StyleStates())
        {
            var f = StorageTriage.Evaluate(s);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(StorageTriage.Ladder(s, f).Select(r => r.Say));
            if (StorageTriage.NotFixedHint(f) is { } hint) texts.Add(hint);
        }
        foreach (var s in ScannerTests.Styled())
        {
            var f = PrinterTriage.Evaluate(s);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(PrinterTriage.Ladder(s, f).Select(r => r.Say));
        }
        texts.Add(Rescan.Rung("audio", "sound device").Say);
        texts.Add(DriverRules.Rollback("sound", "Realtek Audio", "X", Recent())!.Say);
        texts.Add(DriverRules.WindowsUpdate("sound", "Realtek Audio", "X", 10)!.Say);
        return texts.Distinct().Select(t => new object[] { t });
    }

    [Fact]
    public void CoversEveryRungAndFindingKind()
    {
        // Guards the scan itself: if a state stops producing sentences, the style checks below would pass vacuously.
        Assert.True(Sentences().Count() >= 50, $"only {Sentences().Count()} sentences produced");
    }

    [Theory]
    [MemberData(nameof(Sentences))]
    public void FollowsWritingGuide(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        foreach (var c in Contractions)
            Assert.DoesNotContain(c, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"(^|[\s""(])I\s"), text);
        Assert.DoesNotContain("!", text);
        Assert.DoesNotMatch(new Regex(@"\bwe\b", RegexOptions.IgnoreCase), text);
    }

    [Fact]
    public void AdminRungsSayWindowsWillAskForPermission()
    {
        var adminActions = new[] { "service.restart", "device.disable_enable", "device.enable", "network.reset_dns", "network.renew_address", "device.rescan",
            "driver.rollback", "driver.install_from_windows_update", "storage.cleanup_components" };
        var rungs = AudioStates().SelectMany(s => AudioLadder.For(s, AudioTriage.Evaluate(s)))
            .Concat(CameraStates().SelectMany(s => CameraTriage.Ladder(s, CameraTriage.Evaluate(s))))
            .Concat(NetworkStates().SelectMany(s => NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s))))
            .Concat(SlowComputerTests.StyleStates().SelectMany(s => StorageTriage.Ladder(s, StorageTriage.Evaluate(s))))
            .Where(r => adminActions.Contains(r.ActionId)).ToList();
        Assert.NotEmpty(rungs);
        Assert.All(rungs, r => Assert.Contains("Windows will ask for your permission", r.Say));
    }
}
