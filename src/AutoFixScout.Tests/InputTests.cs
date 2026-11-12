using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class InputTests
{
    private static InputState Healthy() => new()
    {
        Devices = new[]
        {
            new InputDevice("HID Keyboard Device", @"HID\VID_1\1", "Keyboard", 0),
            new InputDevice("HID-compliant mouse", @"HID\VID_2\1", "Mouse", 0),
            new InputDevice("HID-compliant touch pad", @"HID\CUST\1", "HIDClass", 0),
        },
        TouchpadEnabled = Tri.Yes, LeaveOnWithMouse = Tri.Yes,
        StickyKeysFlags = 510, FilterKeysFlags = 126, ToggleKeysFlags = 58, MouseKeysFlags = 186,
    };

    private static (IReadOnlyList<Finding> F, IReadOnlyList<Rung> L) Run(InputState s)
    {
        var f = InputTriage.Evaluate(s);
        return (f, InputTriage.Ladder(s, f));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(510, false)]
    [InlineData(511, true)]
    [InlineData(58, false)]
    [InlineData(59, true)]
    public void FlagBit_One_IsOn(int flags, bool on) => Assert.Equal(on, InputState.IsOn(flags));

    [Fact]
    public void FlagBit_MissingValue_IsOff() => Assert.False(InputState.IsOn(null));

    [Theory]
    [InlineData(511, 510)]
    [InlineData(1, 0)]
    [InlineData(510, 510)]
    public void ClearOn_KeepsEveryOtherBit(int before, int after) => Assert.Equal(after, InputState.ClearOn(before));

    [Fact]
    public void Healthy_LooksNormal_OnlyOffersTheMouseSettings()
    {
        var (f, l) = Run(Healthy());
        Assert.Equal(FindingCode.LooksNormal, f[0].Code);
        Assert.Equal("mouse", Assert.Single(l).Arg);
    }

    [Fact]
    public void TouchpadOff_OffersValueThenSettingsPage()
    {
        var s = Healthy(); s.TouchpadEnabled = Tri.No;
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.TouchpadOff, f[0].Code);
        Assert.Equal(new[] { "input.touchpad_on", "ui.open_settings_page" }, l.Select(r => r.ActionId));
        Assert.Equal("touchpad", l[1].Arg);
    }

    [Fact]
    public void TouchpadOff_WithoutATouchpad_IsNotAFinding()
    {
        var s = Healthy(); s.TouchpadEnabled = Tri.No;
        s.Devices = s.Devices.Where(d => !d.IsTouchpad).ToList();
        Assert.Equal(FindingCode.LooksNormal, InputTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void AccessibilityKeys_AreEachFound_AndOneRungClearsThem()
    {
        var s = Healthy(); s.StickyKeysFlags = 511; s.FilterKeysFlags = 127; s.ToggleKeysFlags = 59; s.MouseKeysFlags = 187;
        var (f, l) = Run(s);
        Assert.Equal(new[] { FindingCode.StickyKeysOn, FindingCode.FilterKeysOn, FindingCode.ToggleKeysOn, FindingCode.MouseKeysOn }, f.Select(x => x.Code));
        Assert.Equal(new[] { "input.accessibility_keys_off", "ui.open_settings_page", "ui.open_settings_page" }, l.Select(r => r.ActionId));
        Assert.Equal(new string?[] { null, "keyboard_access", "mouse" }, l.Select(r => r.Arg));
    }

    [Fact]
    public void DeviceErrors_UseRescanAndGuidedSteps_NeverToggleTheDriver()
    {
        var s = Healthy();
        s.Devices = new[] { new InputDevice("Mouse", "A", "Mouse", 22), new InputDevice("Keyboard", "B", "Keyboard", 10) };
        var (f, l) = Run(s);
        Assert.Equal(new[] { FindingCode.InputDeviceDisabled, FindingCode.InputDeviceError }, f.Select(x => x.Code));
        Assert.Contains(l, r => r.ActionId == "device.rescan" && r.Arg == "input");
        Assert.Contains(l, r => r.ActionId == "ui.human_step");
        Assert.DoesNotContain(l, r => r.ActionId is "device.enable" or "device.disable_enable");
        Assert.True(InputTriage.ProbablyHardware(s));
    }

    [Fact]
    public void NoKeyboard_IsFound()
    {
        var s = Healthy(); s.Devices = s.Devices.Where(d => !d.IsKeyboard).ToList();
        Assert.Equal(FindingCode.NoKeyboardFound, Run(s).F[0].Code);
    }

    [Fact]
    public void ProbeErrors_GiveIncomplete_AndNoRungs()
    {
        var s = Healthy(); s.ProbeErrors = new[] { "WMI: x" };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.ProbeIncomplete, f[0].Code);
        Assert.Empty(l);
    }

    [Fact]
    public void Rescan_AreaInput_IsKnownToAppAndExecutor()
    {
        Assert.Contains("input", Rescan.Areas);
        Assert.Contains("input", ExecutorProgram.RescanAreas);
        Assert.Equal(Rescan.Areas, ExecutorProgram.RescanAreas);
    }

    [Fact]
    public void UserActions_NeverAskForAdmin()
    {
        Assert.DoesNotContain("input.touchpad_on", FixActions.ElevatedActions);
        Assert.DoesNotContain("input.accessibility_keys_off", FixActions.ElevatedActions);
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var states = new List<InputState> { Healthy() };
        var a = Healthy(); a.TouchpadEnabled = Tri.No; a.StickyKeysFlags = 511; a.FilterKeysFlags = 127; a.ToggleKeysFlags = 59; a.MouseKeysFlags = 187; states.Add(a);
        var b = Healthy(); b.Devices = new[] { new InputDevice("M", "A", "Mouse", 22), new InputDevice("K", "B", "Keyboard", 10) }; states.Add(b);
        var c = Healthy(); c.Devices = Array.Empty<InputDevice>(); states.Add(c);
        var d = Healthy(); d.ProbeErrors = new[] { "x" }; states.Add(d);
        foreach (var s in states)
        {
            var (f, l) = Run(s);
            foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).Append(InputTriage.NotFixedHint(f) ?? ""))
                Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
            foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
                Assert.Contains("Windows will ask for your permission", r.Say);
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        }
    }
}

[Collection("Live machine")] // one at a time: they change and restore the same settings
public class LiveInputTests
{
    private static bool Live => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"));

    [Fact]
    public void Read_HasNoProbeErrors_AndFindsAKeyboard()
    {
        if (!Live) return;
        var s = InputFlow.Read();
        Assert.Empty(s.ProbeErrors);
        Assert.Contains(s.Devices, d => d.IsKeyboard);
    }

    [Fact]
    public async Task AccessibilityKeysOff_ClearsStickyKeys_AndRestoresTheExactFlags()
    {
        if (!Live) return;
        var kinds = Enum.GetValues<Accessibility.Kind>();
        var before = kinds.ToDictionary(k => k, Accessibility.Get);
        try
        {
            Accessibility.Set(Accessibility.Kind.Sticky, before[Accessibility.Kind.Sticky] | InputState.OnBit);
            var s = InputFlow.Read();
            Assert.Contains(InputTriage.Evaluate(s), x => x.Code == FindingCode.StickyKeysOn);
            var rung = InputTriage.Ladder(s, InputTriage.Evaluate(s)).First(r => r.ActionId == "input.accessibility_keys_off");
            var (ok, detail) = await FixActions.RunAsync(rung);
            Assert.True(ok, detail);
            var after = InputFlow.Read();
            Assert.DoesNotContain(InputTriage.Evaluate(after), x => x.Code == FindingCode.StickyKeysOn);
            Assert.Equal(InputState.ClearOn(before[Accessibility.Kind.Sticky] | InputState.OnBit), after.StickyKeysFlags);
        }
        finally
        {
            foreach (var k in kinds) Accessibility.Set(k, before[k]);
        }
        Assert.Equal(before, kinds.ToDictionary(k => k, Accessibility.Get));
    }

    [Fact]
    public async Task TouchpadOn_WritesTheSetting_AndRestoresIt()
    {
        if (!Live) return;
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RealInputProbe.TouchpadKey + @"\Status", true);
        var old = k.GetValue("Enabled");
        try
        {
            k.SetValue("Enabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            var (ok, detail) = await FixActions.RunAsync(new Rung("t", "input.touchpad_on", 0, ""));
            Assert.True(ok, detail);
            Assert.Equal(Tri.Yes, InputFlow.Read().TouchpadEnabled);
        }
        finally
        {
            if (old is int v) k.SetValue("Enabled", v, Microsoft.Win32.RegistryValueKind.DWord); else k.DeleteValue("Enabled", false);
        }
    }
}
