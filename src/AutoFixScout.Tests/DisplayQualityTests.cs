using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class DisplayQualityTests
{
    private static DisplayMode M(int w, int h, int hz) => new(w, h, hz);

    private static readonly DisplayMode[] Modes =
    {
        M(1366, 768, 60), M(1920, 1080, 60), M(1920, 1080, 144), M(1920, 1080, 120), M(1280, 720, 60), M(2560, 1440, 60),
        M(1366, 768, 144),
    };

    private static ScreenInfo Screen(DisplayMode cur, int? scale = 100, DisplayMode[]? modes = null) =>
        new(@"\.\DISPLAY1", cur, modes ?? Modes, scale);

    private static DisplayState State(params ScreenInfo[] screens) =>
        new() { ConnectedScreens = screens.Length, ShowingScreens = screens.Length, Screens = screens };

    [Fact]
    public void Native_IsLargestByPixelCount()
    {
        Assert.Equal(M(2560, 1440, 60), Screen(M(1366, 768, 60)).Native);
        // 1920 x 1200 (2.30 M) beats 2048 x 1000 (2.05 M) even though it is narrower.
        var modes = new[] { M(2048, 1000, 60), M(1920, 1200, 60) };
        Assert.Equal(M(1920, 1200, 60), Screen(M(1920, 1200, 60), modes: modes).Native);
        Assert.Null(Screen(M(1, 1, 60), modes: Array.Empty<DisplayMode>()).Native);
    }

    [Fact]
    public void BestRefresh_IsHighestAtCurrentResolution()
    {
        Assert.Equal(144, Screen(M(1920, 1080, 60)).BestRefresh);
        Assert.Equal(144, Screen(M(1366, 768, 60)).BestRefresh);
        Assert.Equal(60, Screen(M(1280, 720, 60)).BestRefresh);
        Assert.Null(Screen(M(800, 600, 60)).BestRefresh);
    }

    [Fact]
    public void NotNative_IsReported_AndNativeIsNot()
    {
        var low = DisplayTriage.Evaluate(State(Screen(M(1366, 768, 60))));
        var f = Assert.Single(low, x => x.Code == FindingCode.DisplayNotNativeResolution);
        Assert.Equal("The screen is set to 1366 x 768, but it can show 2560 x 1440. Text and pictures may look blurry.", f.PlainMessage);
        Assert.DoesNotContain(DisplayTriage.Evaluate(State(Screen(M(2560, 1440, 60)))), x => x.Code == FindingCode.DisplayNotNativeResolution);
    }

    [Theory]
    [InlineData(60, 144, true)]
    [InlineData(120, 144, true)]   // 24 Hz gap
    [InlineData(125, 144, false)]  // 19 Hz gap
    [InlineData(124, 144, true)]   // exactly 20 Hz gap
    [InlineData(144, 144, false)]
    public void LowRefresh_Threshold(int current, int best, bool expected)
    {
        var modes = new[] { M(1920, 1080, current), M(1920, 1080, best) };
        var f = DisplayTriage.Evaluate(State(Screen(M(1920, 1080, current), modes: modes)));
        Assert.Equal(expected, f.Any(x => x.Code == FindingCode.DisplayLowRefreshRate));
    }

    [Theory]
    [InlineData(100, 1920, false)]
    [InlineData(125, 1920, false)]
    [InlineData(150, 1366, false)]
    [InlineData(200, 1920, false)]
    [InlineData(250, 1920, true)]   // above 200 on a small screen
    [InlineData(250, 3840, false)]  // fine on a large screen
    [InlineData(133, 1920, true)]   // not a Windows value
    [InlineData(null, 1920, false)] // unknown never flags
    public void UnusualScaling_Conservative(int? scale, int width, bool expected)
    {
        var modes = new[] { M(width, 1080, 60) };
        var f = DisplayTriage.Evaluate(State(Screen(M(width, 1080, 60), scale, modes)));
        Assert.Equal(expected, f.Any(x => x.Code == FindingCode.DisplayUnusualScaling));
    }

    [Fact]
    public void ExistingFindingsComeFirst()
    {
        var s = State(Screen(M(1366, 768, 60)));
        s.ConnectedScreens = 2;
        s.BrightnessPercent = 3;
        var f = DisplayTriage.Evaluate(s);
        Assert.Equal(FindingCode.ExternalDisplayNotShown, f[0].Code);
        Assert.Equal(FindingCode.BrightnessVeryLow, f[1].Code);
        Assert.Equal(FindingCode.DisplayNotNativeResolution, f[2].Code);
    }

    [Fact]
    public void NormalScreen_StillLooksNormal()
    {
        var f = DisplayTriage.Evaluate(State(Screen(M(2560, 1440, 60), 125, new[] { M(2560, 1440, 60) })));
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
    }

    [Fact]
    public void Ladder_OpensSettingsOnly_AndRungIdsAreUnique()
    {
        var s = State(Screen(M(1366, 768, 60), 133));
        var f = DisplayTriage.Evaluate(s);
        var rungs = DisplayTriage.Ladder(s, f);
        Assert.Equal(rungs.Count, rungs.Select(r => r.Id).Distinct().Count());
        Assert.Contains(rungs, r => r.Id == "display_resolution" && r.Say.Contains("Recommended"));
        Assert.Contains(rungs, r => r.Id == "display_refresh" && r.Say.Contains("Advanced display") && r.Say.Contains("Choose a refresh rate"));
        Assert.All(rungs, r => { Assert.Equal("ui.open_settings_page", r.ActionId); Assert.Equal("display", r.Arg); });
        // With the other findings the existing rungs are still offered, ids still unique.
        s.ConnectedScreens = 2; s.BrightnessPercent = 2;
        var all = DisplayTriage.Ladder(s, DisplayTriage.Evaluate(s));
        Assert.Equal(all.Count, all.Select(r => r.Id).Distinct().Count());
        Assert.Contains(all, r => r.Id == "brightness");
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var s = State(Screen(M(1366, 768, 60), 250));
        var f = DisplayTriage.Evaluate(s);
        var texts = f.Select(x => x.PlainMessage).Concat(DisplayTriage.Ladder(s, f).Select(r => r.Say)).ToList();
        Assert.True(texts.Count >= 4);
        var bad = new Regex(@"n't|'ll|'re|'ve|'s |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase);
        foreach (var t in texts) Assert.DoesNotMatch(bad, t);
    }
}

/// <summary>Read-only: reads the real screens (AFS_LIVE set). Never changes a display setting.</summary>
[Collection("Live machine")]
public class LiveDisplayQualityTests
{
    [Fact]
    public void Screens_AreReadAndCurrentModeIsSupported()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = new RealDisplayProbe().Read();
        var f = DisplayTriage.Evaluate(s);
        var lines = new List<string> { "errors: " + string.Join(" | ", s.ProbeErrors) };
        foreach (var scr in s.Screens)
            lines.Add($"{scr.Name}: current {scr.Current.Width}x{scr.Current.Height}@{scr.Current.Refresh} Hz; native {scr.Native?.Width}x{scr.Native?.Height}; " +
                $"best refresh {scr.BestRefresh}; scale {scr.ScalePercent}%; modes {scr.Modes.Count}");
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage} [{x.TechnicalDetail}]"));
        lines.AddRange(DisplayTriage.Ladder(s, f).Select(r => $"RUNG {r.Id}"));
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
        Assert.NotEmpty(s.Screens);
        foreach (var scr in s.Screens)
        {
            Assert.NotEmpty(scr.Modes);
            Assert.Contains(scr.Modes, m => m.Width == scr.Current.Width && m.Height == scr.Current.Height);
        }
    }
}
