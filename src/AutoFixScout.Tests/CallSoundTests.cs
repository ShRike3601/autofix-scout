using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Microsoft.Win32;
using Xunit;

namespace AutoFixScout.Tests;

public class CallSoundTests
{
    private static readonly ServiceInfo[] Running = { new("AudioSrv", "Running"), new("AudioEndpointBuilder", "Running") };
    private static readonly AudioEndpointInfo Speakers = new("Speakers (Realtek)", true, true, "Speakers");

    private static AudioState State(bool callFocus, Ducking? ducking, string? commsId = "mm", string commsName = "Headset (Realtek)") => new()
    {
        Services = Running, Endpoints = new[] { Speakers }, DefaultMuted = Tri.No, DefaultVolumePercent = 50,
        CallFocus = callFocus, Ducking = ducking,
        MultimediaDeviceId = "mm", MultimediaDeviceName = "Speakers (Realtek)", CommsDeviceId = commsId, CommsDeviceName = commsName,
    };

    [Theory]
    [InlineData(null, Ducking.Reduce80)]
    [InlineData(0, Ducking.MuteOthers)]
    [InlineData(1, Ducking.Reduce80)]
    [InlineData(2, Ducking.Reduce50)]
    [InlineData(3, Ducking.DoNothing)]
    public void Decode_KnownValuesAndAbsent(object? raw, Ducking expected) => Assert.Equal(expected, CallSound.Decode(raw));

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData("1")]
    public void Decode_UnknownValue_IsNull(object raw) => Assert.Null(CallSound.Decode(raw));

    [Fact]
    public void WithoutCallFocus_TodaysBehaviourIsKept()
    {
        var f = AudioTriage.Evaluate(State(false, Ducking.Reduce80, "other"));
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
    }

    [Fact]
    public void CallFocus_FindsDucking_ButNotForDoNothingOrUnknown()
    {
        foreach (var d in new[] { Ducking.MuteOthers, Ducking.Reduce80, Ducking.Reduce50 })
        {
            var one = Assert.Single(AudioTriage.Evaluate(State(true, d)));
            Assert.Equal(FindingCode.SoundDuckingDuringCalls, one.Code);
            Assert.Equal("Windows lowers other sounds during calls.", one.PlainMessage);
        }
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(AudioTriage.Evaluate(State(true, Ducking.DoNothing))).Code);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(AudioTriage.Evaluate(State(true, null))).Code);
    }

    [Fact]
    public void CallFocus_FindsDifferentCommsDevice()
    {
        var f = AudioTriage.Evaluate(State(true, Ducking.DoNothing, "comms"));
        var one = Assert.Single(f);
        Assert.Equal(FindingCode.SoundCallDeviceDifferent, one.Code);
        Assert.Equal("Calls use \"Headset (Realtek)\" instead of \"Speakers (Realtek)\".", one.PlainMessage);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(AudioTriage.Evaluate(State(true, Ducking.DoNothing, "mm"))).Code);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(AudioTriage.Evaluate(State(true, Ducking.DoNothing, null))).Code);
    }

    [Fact]
    public void NewCodes_AreAppendedAfterTheOlderOnes()
    {
        // Appended, never inserted: older codes keep their numbers (other branches may append after these).
        Assert.True(FindingCode.SoundDuckingDuringCalls > FindingCode.NetWifiDrops);
        Assert.Equal((int)FindingCode.SoundDuckingDuringCalls + 1, (int)FindingCode.SoundCallDeviceDifferent);
    }

    [Fact]
    public void Ladder_HasDuckingRungAndClassicSoundStep_WithUniqueIds()
    {
        var s = State(true, Ducking.Reduce80, "comms");
        var ladder = AudioLadder.For(s, AudioTriage.Evaluate(s));
        var off = Assert.Single(ladder, r => r.Id == "ducking_off");
        Assert.Equal("audio.ducking_off", off.ActionId);
        Assert.Equal(1, off.Tier);
        Assert.DoesNotContain("audio.ducking_off", FixActions.ElevatedActions);
        var dev = Assert.Single(ladder, r => r.Id == "call_device");
        Assert.Equal("ui.open_settings_page", dev.ActionId);
        Assert.Equal("sound_classic", dev.Arg);
        Assert.Contains("Set as Default Communication Device", dev.Say);
        Assert.Equal(ladder.Count, ladder.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Ladder_WithoutCallFocus_HasNoCallRungs()
    {
        var s = State(false, Ducking.Reduce80, "comms");
        Assert.DoesNotContain(AudioLadder.For(s, AudioTriage.Evaluate(s)), r => r.Id is "ducking_off" or "call_device");
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var s = State(true, Ducking.Reduce50, "comms");
        var f = AudioTriage.Evaluate(s);
        var texts = f.Select(x => x.PlainMessage).Concat(AudioLadder.For(s, f).Select(r => r.Say)).ToList();
        Assert.True(texts.Count >= 4);
        foreach (var t in texts)
        {
            Assert.DoesNotMatch(new Regex(@"n't|'ll|'re|'ve|'s |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase), t);
            Assert.All(t.Split('.', StringSplitOptions.RemoveEmptyEntries), x => Assert.True(x.Split(' ').Length <= 30, x));
        }
    }
}

[Collection("Live machine")]
public class LiveCallSoundTests
{
    [Fact]
    public async Task DuckingOff_SetsThreeAndRestoresExactOriginal()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var lines = new List<string>();
        object? original;
        RegistryValueKind kind = RegistryValueKind.DWord;
        using (var k = Registry.CurrentUser.OpenSubKey(CallSound.KeyPath))
        {
            original = k?.GetValue(CallSound.ValueName);
            if (original != null) kind = k!.GetValueKind(CallSound.ValueName);
        }
        lines.Add($"original: {original ?? "absent"} ({kind})");
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(CallSound.KeyPath, writable: true))
                k.SetValue(CallSound.ValueName, 1, RegistryValueKind.DWord);
            Assert.Equal(Ducking.Reduce80, new RealAudioProbe().Read().Ducking);
            var (ok, detail) = await FixActions.RunAsync(new Rung("ducking_off", "audio.ducking_off", 1, "x"));
            lines.Add($"ducking_off: {ok} {detail}");
            Assert.True(ok, detail);
            using var r = Registry.CurrentUser.OpenSubKey(CallSound.KeyPath)!;
            Assert.Equal(3, r.GetValue(CallSound.ValueName));
            Assert.Contains("before: UserDuckingPreference=1", detail);
            var state = new RealAudioProbe().Read();
            state.CallFocus = true;
            lines.Add("findings with call focus: " + string.Join(", ", AudioTriage.Evaluate(state).Select(f => f.Code)));
            lines.Add($"media default: {state.MultimediaDeviceName}; communications default: {state.CommsDeviceName}");
        }
        finally
        {
            using (var k = Registry.CurrentUser.CreateSubKey(CallSound.KeyPath, writable: true))
            {
                if (original is null) k.DeleteValue(CallSound.ValueName, false);
                else k.SetValue(CallSound.ValueName, original, kind);
            }
            using var chk = Registry.CurrentUser.OpenSubKey(CallSound.KeyPath);
            lines.Add($"restored: {chk?.GetValue(CallSound.ValueName) ?? "absent"}");
            File.WriteAllLines(output, lines);
        }
        using var final = Registry.CurrentUser.OpenSubKey(CallSound.KeyPath);
        Assert.Equal(original, final?.GetValue(CallSound.ValueName));
    }
}
