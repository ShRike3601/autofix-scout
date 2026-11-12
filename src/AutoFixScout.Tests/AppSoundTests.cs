using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using NAudio.CoreAudioApi;
using Xunit;

namespace AutoFixScout.Tests;

public class AppSoundTests
{
    private static readonly ServiceInfo[] Running = { new("AudioSrv", "Running"), new("AudioEndpointBuilder", "Running") };
    private static readonly AudioEndpointInfo Speakers = new("Speakers (Realtek)", true, true, "Speakers");

    private static AudioState Fine(params AudioSessionInfo[] sessions) =>
        new() { Services = Running, Endpoints = new[] { Speakers }, DefaultMuted = Tri.No, DefaultVolumePercent = 50, Sessions = sessions };

    private static readonly AudioSessionInfo MutedApp = new("spotify", "Spotify", 80, true, true);

    [Fact]
    public void MutedApp_IsFound_WhenDeviceIsFine()
    {
        var f = AudioTriage.Evaluate(Fine(MutedApp));
        var one = Assert.Single(f);
        Assert.Equal(FindingCode.AppSoundMuted, one.Code);
        Assert.Equal("Sound is muted for \"Spotify\" in the Volume mixer.", one.PlainMessage);
    }

    [Fact]
    public void VeryLowApp_IsFound_AtFourPercentButNotFive()
    {
        Assert.Equal(FindingCode.AppSoundVeryLow, Assert.Single(AudioTriage.Evaluate(Fine(new AudioSessionInfo("chrome", null, 4, false, false)))).Code);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(AudioTriage.Evaluate(Fine(new AudioSessionInfo("chrome", null, 5, false, false)))).Code);
    }

    [Fact]
    public void DeviceFindings_KeepPriority()
    {
        var s = Fine(MutedApp);
        s.DefaultMuted = Tri.Yes;
        Assert.DoesNotContain(AudioTriage.Evaluate(s), f => f.Code == FindingCode.AppSoundMuted);
        s = Fine(MutedApp);
        s.DefaultVolumePercent = 2;
        Assert.DoesNotContain(AudioTriage.Evaluate(s), f => f.Code is FindingCode.AppSoundMuted or FindingCode.AppSoundVeryLow);
        var hdmi = new AudioEndpointInfo("TV", true, true, "Hdmi");
        s = Fine(MutedApp);
        s.Endpoints = new[] { hdmi, Speakers with { IsDefault = false } };
        Assert.DoesNotContain(AudioTriage.Evaluate(s), f => f.Code == FindingCode.AppSoundMuted);
    }

    [Fact]
    public void NewCodes_AreAppendedAfterTheOlderOnes()
    {
        // Appended, never inserted: older codes keep their numbers.
        Assert.True(FindingCode.AppSoundMuted > FindingCode.ShellPackageDamaged);
        Assert.Equal((int)FindingCode.AppSoundMuted + 1, (int)FindingCode.AppSoundVeryLow);
    }

    [Fact]
    public void Ladder_UnmuteAppThenMixer_AndRungIdsAreUnique()
    {
        var s = Fine(MutedApp);
        var ladder = AudioLadder.For(s, AudioTriage.Evaluate(s));
        Assert.Equal(new[] { "unmute_app", "volume_mixer", "restart_services", "audio_enhancements" }, ladder.Select(r => r.Id));
        Assert.Equal("spotify", ladder[0].Arg);
        Assert.Equal("audio.unmute_app", ladder[0].ActionId);
        Assert.Equal(1, ladder[0].Tier);
        Assert.Equal("volume_mixer", ladder[1].Arg);
        Assert.Equal(ladder.Count, ladder.Select(r => r.Id).Distinct().Count());
        Assert.DoesNotContain("audio.unmute_app", FixActions.ElevatedActions);
    }

    [Fact]
    public void Ladder_InvalidProcessName_OffersNoUnmute()
    {
        var s = Fine(new AudioSessionInfo("my app; calc", null, 50, true, true));
        var ladder = AudioLadder.For(s, AudioTriage.Evaluate(s));
        Assert.DoesNotContain(ladder, r => r.ActionId == "audio.unmute_app");
        Assert.Contains(ladder, r => r.Id == "volume_mixer");
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("Microsoft.Teams_2-x", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("my app", false)]
    [InlineData("a;b", false)]
    [InlineData("a&b", false)]
    [InlineData("..\\x", false)]
    [InlineData("a/b", false)]
    [InlineData("a\nb", false)]
    public void NameValidation(string? name, bool valid) => Assert.Equal(valid, AppSound.IsValidName(name));

    [Fact]
    public async Task Action_RejectsInvalidAndUnknownNames()
    {
        var bad = await FixActions.RunAsync(new Rung("unmute_app", "audio.unmute_app", 1, "x", "a;b"));
        Assert.False(bad.Ok);
        var none = await FixActions.RunAsync(new Rung("unmute_app", "audio.unmute_app", 1, "x", "no-such-app-xyz"));
        Assert.False(none.Ok);
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var texts = new List<string>();
        foreach (var s in new[] { Fine(MutedApp, new AudioSessionInfo("chrome", null, 2, false, false)), Fine(new AudioSessionInfo("chrome", null, 2, false, false)) })
        {
            var f = AudioTriage.Evaluate(s);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(AudioLadder.For(s, f).Select(r => r.Say));
        }
        Assert.True(texts.Count >= 6);
        foreach (var t in texts)
        {
            Assert.DoesNotMatch(new Regex(@"n't|'ll|'re|'ve|'s |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase), t);
            Assert.All(t.Split('.', StringSplitOptions.RemoveEmptyEntries), x => Assert.True(x.Split(' ').Length <= 30, x));
        }
    }
}

[Collection("Live machine")]
public class LiveAppSoundTests
{
    [Fact]
    public async Task ReadsSessions_AndUnmuteRestoresExactValues()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var state = new RealAudioProbe().Read();
        var lines = new List<string> { "session error: " + state.SessionError, "device muted " + state.DefaultMuted + " volume " + state.DefaultVolumePercent };
        lines.AddRange(state.Sessions.Select(s => $"SESSION {s.Process} / {s.DisplayName} vol={s.VolumePercent} muted={s.Muted} active={s.Active}"));
        File.WriteAllLines(output, lines);
        Assert.Null(state.SessionError);
        Assert.Empty(state.ProbeErrors);

        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = dev.AudioSessionManager.Sessions;
        AudioSessionControl? target = null;
        string? name = null;
        for (var i = 0; i < sessions.Count && target is null; i++)
        {
            var s = sessions[i];
            if (s.IsSystemSoundsSession) continue;
            try { name = System.Diagnostics.Process.GetProcessById((int)s.GetProcessID).ProcessName; } catch { continue; }
            if (AppSound.IsValidName(name)) target = s;
        }
        if (target is null) { lines.Add("no app session present, mute/unmute step skipped"); File.WriteAllLines(output, lines); return; }

        var vol = target.SimpleAudioVolume;
        var (prevMute, prevVol) = (vol.Mute, vol.Volume);
        try
        {
            vol.Mute = true;
            vol.Volume = 0.02f;
            var s2 = new RealAudioProbe().Read();
            Assert.Contains(s2.Sessions, x => x.Process == name && x.Muted);
            var (ok, detail) = await FixActions.RunAsync(new Rung("unmute_app", "audio.unmute_app", 1, "x", name));
            lines.Add($"unmute_app {name}: {ok} {detail}");
            Assert.True(ok, detail);
            Assert.False(vol.Mute);
            Assert.True(vol.Volume > 0.99f);
        }
        finally
        {
            vol.Volume = prevVol;
            vol.Mute = prevMute;
            lines.Add($"restored mute={vol.Mute} volume={vol.Volume} (was {prevMute}, {prevVol})");
            File.WriteAllLines(output, lines);
        }
        Assert.Equal(prevMute, vol.Mute);
        Assert.Equal(prevVol, vol.Volume, 3);
    }
}
