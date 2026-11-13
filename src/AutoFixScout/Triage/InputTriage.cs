using AutoFixScout.Probes;

namespace AutoFixScout.Triage;

/// <summary>
/// "My keyboard, mouse or touchpad is not working". Pure local logic. Device drivers for keyboards and mice are never
/// disabled or restarted by the app, because that could leave the person without any way to type or point.
/// </summary>
public static class InputTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string Incomplete = "AutoFix Scout could not complete the check of the keyboard, mouse and touchpad.";

    private static string Names(IEnumerable<InputDevice> d) => string.Join(", ", d.Select(x => x.Name).Distinct().Take(4));

    public static IReadOnlyList<Finding> Evaluate(InputState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)) };
        var f = new List<Finding>();

        if (!s.Devices.Any(d => d.IsKeyboard))
            f.Add(F(FindingCode.NoKeyboardFound, "Windows cannot find a keyboard at the moment.", "no Keyboard-class device"));
        var disabled = s.Devices.Where(d => d.Disabled).ToList();
        if (disabled.Count > 0)
            f.Add(F(FindingCode.InputDeviceDisabled, "A keyboard, mouse or touchpad is switched off in Windows.", Names(disabled) + " problem code 22"));
        var errored = s.Devices.Where(d => d.Errored).ToList();
        if (errored.Count > 0)
            f.Add(F(FindingCode.InputDeviceError, "Windows reports a problem with a keyboard, mouse or touchpad.",
                string.Join(", ", errored.Select(d => $"{d.Name} problem code {d.ProblemCode}").Distinct().Take(4))));

        if (s.TouchpadEnabled == Tri.No && s.HasTouchpad)
            f.Add(F(FindingCode.TouchpadOff, "The touchpad is switched off in Windows settings.",
                "PrecisionTouchPad Enabled=0" + (s.HasMouse ? "; a mouse is connected" : "")));
        if (InputState.IsOn(s.StickyKeysFlags))
            f.Add(F(FindingCode.StickyKeysOn, "\"Sticky Keys\" is on, so keys such as Shift and Ctrl stay pressed until the next key is typed.", $"StickyKeys flags {s.StickyKeysFlags}"));
        if (InputState.IsOn(s.FilterKeysFlags))
            f.Add(F(FindingCode.FilterKeysOn, "\"Filter Keys\" is on, so Windows ignores short or repeated key presses and the keyboard seems slow.", $"FilterKeys flags {s.FilterKeysFlags}"));
        if (InputState.IsOn(s.ToggleKeysFlags))
            f.Add(F(FindingCode.ToggleKeysOn, "\"Toggle Keys\" is on, so Windows plays a sound whenever Caps Lock, Num Lock or Scroll Lock is pressed.", $"ToggleKeys flags {s.ToggleKeysFlags}"));
        if (InputState.IsOn(s.MouseKeysFlags))
            f.Add(F(FindingCode.MouseKeysOn, "\"Mouse Keys\" is on, so the number keys move the pointer instead of typing numbers.", $"MouseKeys flags {s.MouseKeysFlags}"));

        var layoutFindings = new List<Finding>();
        if (s.Layouts.Count >= 2 && s.SwitchHotkeyOn)
            layoutFindings.Add(F(FindingCode.InputSeveralLayouts,
                "Several keyboard layouts are installed and a switch key combination is on, so pressing it by accident changes the layout. Typing may produce the wrong characters.",
                $"{s.Layouts.Count} layouts: {string.Join(", ", s.Layouts.Select(l => l.Name).Take(6))}; language hotkey {s.LanguageHotkey}, layout hotkey {s.LayoutHotkey}"));
        if (s.DefaultLayout is { } dl && s.DisplayLanguage is { Length: > 0 } dispLang
            && KeyboardLayouts.LanguageOfLangId(dl.LangId) is { } layoutLang && layoutLang != KeyboardLayouts.LanguageOfTag(dispLang))
            layoutFindings.Add(F(FindingCode.InputLayoutMismatch,
                "The default keyboard layout belongs to a different language than the Windows display language. This is not a fault, but it can explain unexpected characters.",
                $"default layout {dl.Klid} ({dl.Name}), display language {dispLang}"));
        f = s.TypingFocus ? layoutFindings.Concat(f).ToList() : f.Concat(layoutFindings).ToList();

        if (f.Count > 0) return f;
        return new[] { F(FindingCode.LooksNormal, "Windows reports no problem with the keyboard, mouse or touchpad, and no accessibility key feature is on.",
            $"{s.Devices.Count(d => d.IsKeyboard)} keyboard(s), {s.Devices.Count(d => d.IsMouse)} mouse device(s)") };
    }

    /// <summary>Least risky first.</summary>
    public static IReadOnlyList<Rung> Ladder(InputState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var r = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return r;

        if (codes.Contains(FindingCode.TouchpadOff))
        {
            r.Add(new("touchpad_on", "input.touchpad_on", 0,
                "AutoFix Scout can switch the touchpad back on. You can switch it off again at any time in \"Settings\", \"Bluetooth & devices\", \"Touchpad\"."));
            r.Add(new("touchpad_settings", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Touchpad\" settings. Please make sure the \"Touchpad\" switch is on.", "touchpad"));
        }
        var accessibility = new[] { FindingCode.StickyKeysOn, FindingCode.FilterKeysOn, FindingCode.ToggleKeysOn, FindingCode.MouseKeysOn };
        if (accessibility.Any(codes.Contains))
        {
            r.Add(new("accessibility_keys_off", "input.accessibility_keys_off", 0,
                "AutoFix Scout can switch off \"Sticky Keys\", \"Filter Keys\", \"Toggle Keys\" and \"Mouse Keys\" where they are on. " +
                "Other keyboard accessibility settings are not changed, and the features can be switched on again at any time."));
            r.Add(new("keyboard_access_settings", "ui.open_settings_page", 0,
                "AutoFix Scout will open the keyboard accessibility settings. Please make sure the features you do not use are switched off.", "keyboard_access"));
            if (codes.Contains(FindingCode.MouseKeysOn))
                r.Add(new("mouse_settings", "ui.open_settings_page", 0,
                    "AutoFix Scout will open the mouse settings, where the pointer and the touchpad can be checked.", "mouse"));
        }
        if (codes.Contains(FindingCode.InputDeviceDisabled) || codes.Contains(FindingCode.InputDeviceError) || codes.Contains(FindingCode.NoKeyboardFound))
        {
            r.Add(new("replug_device", "ui.human_step", 0,
                "Please unplug the keyboard or mouse, wait a few seconds, and plug it into a different USB port. " +
                "For a wireless device, please also check the batteries, the on and off switch, and that the small wireless receiver is plugged in firmly."));
            r.Add(Rescan.Rung("input", "keyboard or mouse"));
        }
        if (codes.Contains(FindingCode.InputSeveralLayouts) || codes.Contains(FindingCode.InputLayoutMismatch))
        {
            var layoutRungs = new List<Rung>
            {
                new("language_settings", "ui.open_settings_page", 0,
                    "AutoFix Scout will open the \"Language & region\" settings. Please select the language, choose \"Language options\" and remove the keyboard layouts that you do not need. Nothing is changed without your action.", "language"),
            };
            if (codes.Contains(FindingCode.InputSeveralLayouts))
            {
                layoutRungs.Add(new("typing_settings", "ui.open_settings_page", 0,
                    "AutoFix Scout will open the \"Typing\" settings. Please select \"Advanced keyboard settings\", then \"Input language hot keys\", and switch the key combination off if you do not use it.", "typing"));
                layoutRungs.Add(new("layout_tip", "ui.human_step", 0,
                    "A tip: the keys Windows and Space together show the list of keyboard layouts and let you choose one on purpose. The layout in use is also shown at the right end of the taskbar."));
            }
            if (s.TypingFocus) r.InsertRange(0, layoutRungs); else r.AddRange(layoutRungs);
        }
        if (r.Count == 0)
            r.Add(new("mouse_settings", "ui.open_settings_page", 0,
                "AutoFix Scout will open the mouse and touchpad settings so that the pointer settings can be checked.", "mouse"));
        return r;
    }

    /// <summary>A keyboard or mouse that Windows reports as failing, or none at all, points at the device or its cable.</summary>
    public static bool ProbablyHardware(InputState s) =>
        s.ProbeErrors.Count == 0 && (!s.Devices.Any(d => d.IsKeyboard) || s.Devices.Any(d => d.Errored));

    public static string? NotFixedHint(IReadOnlyList<Finding> after) =>
        after.Any(x => x.Code is FindingCode.NoKeyboardFound or FindingCode.InputDeviceError)
            ? "If the problem remains, please try the keyboard or mouse on another computer. If it does not work there either, the device itself is probably faulty."
            : null;
}
