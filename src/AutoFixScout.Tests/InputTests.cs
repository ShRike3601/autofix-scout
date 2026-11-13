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

public class InputLayoutTests
{
    private static KeyboardLayoutInfo L(string klid) => new(klid, KeyboardLayouts.FriendlyName(klid));

    private static InputState State(bool focus = false, SwitchHotkey hk = SwitchHotkey.AltShift, string disp = "en-US", params string[] klids)
    {
        if (klids.Length == 0) klids = new[] { "00000409", "00000809" };
        return new InputState
        {
            Devices = new[] { new InputDevice("K", "A", "Keyboard", 0) },
            StickyKeysFlags = 510, FilterKeysFlags = 126, ToggleKeysFlags = 58, MouseKeysFlags = 186,
            Layouts = klids.Select(L).ToList(), DefaultLayout = L(klids[0]), DisplayLanguage = disp,
            LanguageHotkey = hk, LayoutHotkey = hk, TypingFocus = focus,
        };
    }

    [Theory]
    [InlineData("00000409", "US")]
    [InlineData("00000809", "United Kingdom")]
    [InlineData("00004009", "India")]
    public void Klid_FriendlyNames(string klid, string name) => Assert.Equal(name, KeyboardLayouts.FriendlyName(klid));

    [Theory]
    [InlineData("00000409", 0x409)]
    [InlineData("d0010409", 0x409)]
    [InlineData("a0000809", 0x809)]
    public void Klid_LangId(string klid, int id) => Assert.Equal(id, KeyboardLayouts.LangId(klid));

    [Theory]
    [InlineData("409")]
    [InlineData("zzzzzzzz")]
    [InlineData(null)]
    public void Klid_Invalid_IsRejected(string? klid)
    {
        Assert.False(KeyboardLayouts.TryParseKlid(klid, out _));
        Assert.Equal(0, KeyboardLayouts.LangId(klid ?? ""));
    }

    [Theory]
    [InlineData("1", SwitchHotkey.AltShift)]
    [InlineData("2", SwitchHotkey.CtrlShift)]
    [InlineData("3", SwitchHotkey.None)]
    [InlineData("4", SwitchHotkey.Grave)]
    [InlineData("9", SwitchHotkey.Unknown)]
    [InlineData(null, SwitchHotkey.Unknown)]
    public void Hotkey_Decoding(string? v, SwitchHotkey h) => Assert.Equal(h, KeyboardLayouts.DecodeHotkey(v));

    [Fact]
    public void SeveralLayouts_WithAltShift_IsReported()
    {
        var f = InputTriage.Evaluate(State());
        Assert.Contains(f, x => x.Code == FindingCode.InputSeveralLayouts);
        Assert.DoesNotContain(f, x => x.Code == FindingCode.InputLayoutMismatch);
    }

    [Theory]
    [InlineData(SwitchHotkey.None)]
    [InlineData(SwitchHotkey.Grave)]
    public void SeveralLayouts_WithoutKeyChord_IsNotReported(SwitchHotkey hk) =>
        Assert.DoesNotContain(InputTriage.Evaluate(State(hk: hk)), x => x.Code == FindingCode.InputSeveralLayouts);

    [Fact]
    public void OneLayout_IsNotReported() =>
        Assert.DoesNotContain(InputTriage.Evaluate(State(klids: "00000409")), x => x.Code == FindingCode.InputSeveralLayouts);

    [Fact]
    public void Mismatch_DefaultLayoutLanguageDiffersFromDisplayLanguage()
    {
        var f = InputTriage.Evaluate(State(hk: SwitchHotkey.None, disp: "de-DE", klids: "00000409"));
        Assert.Contains(f, x => x.Code == FindingCode.InputLayoutMismatch);
        Assert.DoesNotContain(InputTriage.Evaluate(State(hk: SwitchHotkey.None, disp: "en-GB", klids: "00000409")), x => x.Code == FindingCode.InputLayoutMismatch);
    }

    [Fact]
    public void TypingFocus_PutsLayoutFindingsFirst()
    {
        var s = State(); s.StickyKeysFlags = 511;
        Assert.Equal(FindingCode.StickyKeysOn, InputTriage.Evaluate(s)[0].Code);
        s.TypingFocus = true;
        Assert.Equal(FindingCode.InputSeveralLayouts, InputTriage.Evaluate(s)[0].Code);
        var l = InputTriage.Ladder(s, InputTriage.Evaluate(s));
        Assert.Equal("language_settings", l[0].Id);
    }

    [Fact]
    public void Ladder_OffersSettingsPagesAndTip_AndSentencesFollowTheGuide()
    {
        foreach (var s in new[] { State(), State(true), State(hk: SwitchHotkey.None, disp: "de-DE", klids: "00000409") })
        {
            var f = InputTriage.Evaluate(s);
            var l = InputTriage.Ladder(s, f);
            Assert.Contains(l, r => r.Arg == "language");
            foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)))
                Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        }
        var both = InputTriage.Ladder(State(), InputTriage.Evaluate(State()));
        Assert.Contains(both, r => r.Arg == "typing");
        Assert.Contains(both, r => r.ActionId == "ui.human_step" && r.Say.Contains("Windows and Space"));
    }
}

[Collection("Live machine")]
public class LiveLayoutTests
{
    [Fact]
    public void Read_Layouts_IsReadOnlyAndSensible()
    {
        var path = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(path)) return;
        var s = InputFlow.Read();
        Assert.DoesNotContain(s.ProbeErrors, e => e.StartsWith("Layouts"));
        Assert.NotEmpty(s.Layouts);
        Assert.NotNull(s.DefaultLayout);
        Assert.False(string.IsNullOrEmpty(s.DisplayLanguage));
        var f = InputTriage.Evaluate(s);
        File.AppendAllText(path, $"G3 layouts: {string.Join(", ", s.Layouts.Select(l => l.Klid + " " + l.Name))}; default {s.DefaultLayout?.Name}; display {s.DisplayLanguage}; hotkeys {s.LanguageHotkey}/{s.LayoutHotkey}; findings {string.Join(",", f.Select(x => x.Code))}\n");
    }
}
