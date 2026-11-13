using AutoFixScout.Probes;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>Keyboard, mouse and touchpad repairs that only touch the person's own settings (no admin). Old values are saved first.</summary>
public static class InputRepairs
{
    /// <summary>
    /// input.touchpad_on: PrecisionTouchPad\Status Enabled = 1, the value the "Touchpad" switch in Settings writes.
    /// The settings page remains the next step in the ladder in case Windows does not apply the value at once.
    /// </summary>
    public static (bool Ok, string Detail) TouchpadOn()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RealInputProbe.TouchpadKey + @"\Status", writable: true);
        var before = k.GetValue("Enabled");
        var saved = UserRepairs.Backup("touchpad", $"Enabled={before}\n");
        k.SetValue("Enabled", 1, RegistryValueKind.DWord);
        return (k.GetValue("Enabled") is 1, $"touchpad switched on; previous value saved to {saved}");
    }

    /// <summary>input.accessibility_keys_off: clears only the "on" bit of Sticky, Filter, Toggle and Mouse Keys; applies at once.</summary>
    public static (bool Ok, string Detail) AccessibilityKeysOff()
    {
        var before = new List<string>();
        var changed = new List<string>();
        foreach (var k in Enum.GetValues<Accessibility.Kind>())
        {
            var flags = Accessibility.Get(k);
            before.Add($"{k}={flags}");
            if (!InputState.IsOn(flags)) continue;
            Accessibility.Set(k, InputState.ClearOn(flags));
            changed.Add(k.ToString());
        }
        var saved = UserRepairs.Backup("accessibility-keys", string.Join("\n", before) + "\n");
        var ok = Enum.GetValues<Accessibility.Kind>().All(k => !InputState.IsOn(Accessibility.Get(k)));
        return (ok, $"switched off: {(changed.Count == 0 ? "nothing was on" : string.Join(", ", changed))}; previous flags saved to {saved}");
    }
}
