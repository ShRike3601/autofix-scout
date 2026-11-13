using Microsoft.Win32;

namespace AutoFixScout;

/// <summary>What Windows does to other sounds while a call is active (HKCU\Software\Microsoft\Multimedia\Audio UserDuckingPreference).</summary>
public enum Ducking { MuteOthers = 0, Reduce80 = 1, Reduce50 = 2, DoNothing = 3 }

/// <summary>Communications "ducking" preference: decoding, reading and the non-admin switch to "Do nothing".</summary>
public static class CallSound
{
    public const string KeyPath = @"Software\Microsoft\Multimedia\Audio";
    public const string ValueName = "UserDuckingPreference";

    /// <summary>
    /// Decodes the registry value. An absent value is the Windows default (reduce by 80%).
    /// Null = present but not a known setting, so nothing is claimed about it.
    /// </summary>
    public static Ducking? Decode(object? raw) => raw switch
    {
        null => Ducking.Reduce80,
        int i when i is >= 0 and <= 3 => (Ducking)i,
        _ => null,
    };

    /// <summary>True when Windows lowers or mutes other sounds during calls.</summary>
    public static bool LowersOthers(Ducking? d) => d is Ducking.MuteOthers or Ducking.Reduce80 or Ducking.Reduce50;

    public static string Describe(Ducking d) => d switch
    {
        Ducking.MuteOthers => "mutes all other sounds",
        Ducking.Reduce80 => "reduces other sounds by 80%",
        Ducking.Reduce50 => "reduces other sounds by 50%",
        _ => "does nothing",
    };

    public static Ducking? Read() => Read(Registry.CurrentUser);

    internal static Ducking? Read(RegistryKey hkcu)
    {
        using var k = hkcu.OpenSubKey(KeyPath);
        return Decode(k?.GetValue(ValueName));
    }

    /// <summary>audio.ducking_off: sets "Do nothing" for the current user (no admin). Saves the previous value first and returns it.</summary>
    public static (bool Ok, string Detail) DuckingOff() => DuckingOff(Registry.CurrentUser);

    internal static (bool Ok, string Detail) DuckingOff(RegistryKey hkcu)
    {
        using var k = hkcu.CreateSubKey(KeyPath, writable: true);
        var kind = k.GetValueKind(ValueName, out var previous);
        var before = previous is null ? "absent" : $"{previous} ({kind})";
        var saved = Repair.UserRepairs.Backup("ducking", $"{ValueName}={before}\n");
        k.SetValue(ValueName, (int)Ducking.DoNothing, RegistryValueKind.DWord);
        return (Read(hkcu) == Ducking.DoNothing, $"before: {ValueName}={before}; set to 3 (do nothing); previous value saved to {saved}");
    }

    private static RegistryValueKind GetValueKind(this RegistryKey k, string name, out object? value)
    {
        value = k.GetValue(name);
        return value is null ? RegistryValueKind.Unknown : k.GetValueKind(name);
    }
}
