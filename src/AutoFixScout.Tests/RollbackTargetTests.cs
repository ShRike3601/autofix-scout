using AutoFixScout.Drivers;
using AutoFixScout.Executor;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>Test step 5: the sound flow offered to roll back the HDMI audio driver when the problem was the laptop speakers.</summary>
public class RollbackTargetTests
{
    private const string RealtekId = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_1462\4&1&0001";
    private const string IntelHdmiId = @"HDAUDIO\FUNC_01&VEN_8086&DEV_2812&SUBSYS_80860101\4&1&0002";
    private const string NvidiaHdmiId = @"HDAUDIO\FUNC_01&VEN_10DE&DEV_009F&SUBSYS_1462\5&2&0001";

    private static DriverStatus Recent() => new("2.0", DateTime.UtcNow.AddDays(-3), "1.0");

    private static AudioDeviceInfo Dev(string name, string id, bool recent, int problem = 0) =>
        new(name, id, problem, null, null, null, recent ? Recent() : null);

    private static AudioState State(AudioEndpointInfo[] endpoints, params AudioDeviceInfo[] devices) => new()
    {
        Services = new[] { new ServiceInfo("AudioSrv", "Running"), new ServiceInfo("AudioEndpointBuilder", "Running") },
        Endpoints = endpoints, Devices = devices, DefaultMuted = Tri.No, DefaultVolumePercent = 50,
    };

    private static string? RollbackArg(AudioState s) =>
        AudioLadder.For(s, AudioTriage.Evaluate(s)).SingleOrDefault(r => r.Id == "rollback_driver")?.Arg;

    [Fact]
    public void DefaultOnSpeakers_HdmiDriverWithOlderPackage_NotOffered()
    {
        var s = State(new[] { new AudioEndpointInfo("Speakers (Realtek)", true, true, "Speakers", RealtekId) },
            Dev("Realtek Audio", RealtekId, recent: false), Dev("Intel Display Audio", IntelHdmiId, recent: true));
        Assert.Null(RollbackArg(s));
    }

    [Fact]
    public void DefaultOnSpeakers_ItsOwnDriverIsTheTarget()
    {
        var s = State(new[] { new AudioEndpointInfo("Speakers (Realtek)", true, true, "Speakers", RealtekId) },
            Dev("Intel Display Audio", IntelHdmiId, recent: true), Dev("Realtek Audio", RealtekId, recent: true));
        Assert.Equal(RealtekId, RollbackArg(s));
    }

    [Fact]
    public void DefaultKnown_OtherNonHdmiDeviceNotOffered_UnlessItReportsAnError()
    {
        const string usbId = @"USB\VID_0D8C&PID_0014&MI_00\6&1&0000";
        var ep = new[] { new AudioEndpointInfo("Speakers (Realtek)", true, true, "Speakers", RealtekId) };
        Assert.Null(RollbackArg(State(ep, Dev("Realtek Audio", RealtekId, false), Dev("USB Headset", usbId, true))));
        // The findings blame the headset (it reports an error), so it comes first.
        Assert.Equal(usbId, RollbackArg(State(ep, Dev("Realtek Audio", RealtekId, true), Dev("USB Headset", usbId, true, problem: 10))));
    }

    [Fact]
    public void DefaultDeviceUnknown_FallsBackButNeverToHdmiWhenListeningOnSpeakers()
    {
        var ep = new[] { new AudioEndpointInfo("Speakers (Realtek)", true, true, "Speakers") };
        Assert.Null(RollbackArg(State(ep, Dev("Realtek Audio", RealtekId, false), Dev("Intel Display Audio", IntelHdmiId, true))));
        Assert.Equal(RealtekId, RollbackArg(State(ep, Dev("NVIDIA High Definition Audio", NvidiaHdmiId, true), Dev("Realtek Audio", RealtekId, true))));
    }

    [Fact]
    public void DefaultIsHdmi_ThatDeviceCanBeTheTarget()
    {
        // Known device behind the HDMI output.
        var known = new[] { new AudioEndpointInfo("LG TV (Intel Display Audio)", true, true, "Hdmi", IntelHdmiId) };
        Assert.Equal(IntelHdmiId, RollbackArg(State(known, Dev("Realtek Audio", RealtekId, false), Dev("Intel Display Audio", IntelHdmiId, true))));
        // Unknown device, but the output is HDMI: display audio is fair game.
        var unknown = new[] { new AudioEndpointInfo("LG TV", true, true, "Hdmi") };
        Assert.Equal(IntelHdmiId, RollbackArg(State(unknown, Dev("Realtek Audio", RealtekId, false), Dev("Intel Display Audio", IntelHdmiId, true))));
        // Default on NVIDIA HDMI: Intel HDMI is a different device, never offered.
        var nvidia = new[] { new AudioEndpointInfo("Monitor (NVIDIA)", true, true, "Hdmi", NvidiaHdmiId) };
        Assert.Null(RollbackArg(State(nvidia, Dev("NVIDIA High Definition Audio", NvidiaHdmiId, false), Dev("Intel Display Audio", IntelHdmiId, true))));
    }

    [Theory]
    [InlineData("Intel(R) Display Audio", IntelHdmiId, true)]
    [InlineData("NVIDIA High Definition Audio", NvidiaHdmiId, true)]
    [InlineData("AMD High Definition Audio Device", @"HDAUDIO\FUNC_01&VEN_1002&DEV_AA01&SUBSYS_00AA0100\5&1&0001", true)]
    [InlineData("Intel Smart Sound HDMI", @"INTELAUDIO\FUNC_01&VEN_8086&DEV_2812&SUBSYS_1462\5&1&0201", true)]
    [InlineData("Realtek(R) Audio", RealtekId, false)]
    [InlineData("Realtek(R) Audio", @"INTELAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_1462\5&1&0001", false)]
    [InlineData("Intel Smart Sound Technology for USB Audio", @"PCI\VEN_8086&DEV_A0C8&SUBSYS_1462\3&1&FB", false)]
    [InlineData("JBL Go 3", @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001\7&1&0", false)]
    public void DisplayAudioDetection(string name, string id, bool expected) =>
        Assert.Equal(expected, AudioLadder.IsDisplayAudio(new AudioDeviceInfo(name, id, 0, null, null, null)));

    [Fact]
    public void EndpointMapsToNearestKnownAncestor()
    {
        var devices = new[] { RealtekId, IntelHdmiId };
        Assert.Equal(RealtekId, RealAudioProbe.MatchDevice(new[] { RealtekId.ToLowerInvariant(), @"PCI\VEN_8086\3&1" }, devices));
        Assert.Equal(RealtekId, RealAudioProbe.MatchDevice(new[] { @"INTELAUDIO\CTLR\1", RealtekId }, devices));
        Assert.Null(RealAudioProbe.MatchDevice(new[] { @"BTHENUM\X\1", @"BTH\MS_BTHBRB\1" }, devices));
    }

    [Fact]
    public void EndpointNode_IsBuiltFromTheEndpointId()
    {
        // AFS-TESTBENCH: the endpoint property store had no PKEY_Device_InstanceId, so no output was ever matched.
        Assert.Equal(@"SWD\MMDEVAPI\{0.0.0.00000000}.{96c183e6-a44b-46ee-922e-8e357ddef2db}",
            RealAudioProbe.EndpointNodeId("{0.0.0.00000000}.{96c183e6-a44b-46ee-922e-8e357ddef2db}"));
    }
}

/// <summary>Save point retention: restored ones go at once, settled/orphan ones after 30 days, open or failed ones never.</summary>
public class SavePointRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "afs-sp-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    public SavePointRetentionTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private string Make(int daysOld, string? id = null, bool manifest = true, bool restoreFailed = false)
    {
        id ??= Guid.NewGuid().ToString("N");
        var dir = Directory.CreateDirectory(Path.Combine(_root, id, "driver")).Parent!.FullName;
        File.WriteAllText(Path.Combine(dir, "driver", "x.inf"), "[Version]");
        File.SetAttributes(Path.Combine(dir, "driver", "x.inf"), FileAttributes.ReadOnly);
        if (manifest) File.WriteAllText(Path.Combine(dir, "savepoint.json"), "{}");
        if (restoreFailed) File.WriteAllText(Path.Combine(dir, SavePoints.RestoreFailedFlag), "x");
        Directory.SetCreationTimeUtc(dir, Now.AddDays(-daysOld));
        return id;
    }

    [Fact]
    public void Prune_OnlyOldSettledAndOrphans()
    {
        var oldSettled = Make(40);
        var oldOrphan = Make(40, manifest: false);
        var young = Make(5);
        var failed = Make(90, restoreFailed: true);
        var open = Make(90);
        var notOurs = Make(90, id: "keep-me");
        var prunable = SavePoints.Prunable(_root, Now, id => id == open);
        Assert.Equal(new[] { oldSettled, oldOrphan }.OrderBy(x => x), prunable.OrderBy(x => x));

        SavePoints.Prune(_root, Now, id => id == open);
        Assert.False(Directory.Exists(Path.Combine(_root, oldSettled)));   // read-only driver file didn't stop it
        Assert.False(Directory.Exists(Path.Combine(_root, oldOrphan)));
        foreach (var kept in new[] { young, failed, open, notOurs })
            Assert.True(Directory.Exists(Path.Combine(_root, kept)), kept);
    }

    [Fact]
    public void Delete_RefusesAnythingButA32HexIdInsideTheRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "afs-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.False(SavePoints.Delete(_root, @"..\" + Path.GetFileName(outside)));
            Assert.False(SavePoints.Delete(_root, "0123456789ABCDEF0123456789ABCDEF"));
            Assert.True(Directory.Exists(outside));
        }
        finally { Directory.Delete(outside); }
        var id = Make(0);
        Assert.True(SavePoints.Delete(_root, id));
        Assert.False(Directory.Exists(Path.Combine(_root, id)));
    }

    [Fact]
    public void Restore_WithoutManifest_NothingToPutBack_AndCleansUp()
    {
        var never = Make(0, manifest: false); // crash after the export, before the manifest: nothing was changed
        Assert.Equal(SavePoints.NothingToDo, SavePoints.Restore(_root, never));
        Assert.False(Directory.Exists(Path.Combine(_root, never)));
        // Already restored and deleted (e.g. the app closed before clearing its marker): still settled, not an error.
        Assert.Equal(SavePoints.NothingToDo, SavePoints.Restore(_root, Guid.NewGuid().ToString("N")));
    }
}
