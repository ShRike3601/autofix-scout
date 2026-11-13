using System.Text.RegularExpressions;

namespace AutoFixScout.Probes;

/// <summary>A device with a Device Manager warning (Win32_PnPEntity.ConfigManagerErrorCode other than 0).</summary>
public sealed record DeviceProblem(string Name, string DeviceId, string PnpClass, int Code)
{
    /// <summary>Code 22: the person (or an app) switched the device off. Information only.</summary>
    public bool DisabledByPerson => Code == 22;
    public bool IsUsb => DeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || PnpClass.Equals("USB", StringComparison.OrdinalIgnoreCase);
    /// <summary>Codes for which the driver is missing, damaged or wrong: an optional driver update can help.</summary>
    public bool NeedsDriver => Code is 1 or 18 or 28;
}

public static class DeviceErrors
{
    /// <summary>Code 45 ("not connected") and 0 are noise; code 22 is reported as information only.</summary>
    public static bool IsNoise(int code) => code is 0 or 45;

    /// <summary>A short, formal explanation of a Device Manager error code, to follow the device name.</summary>
    public static string Meaning(int code) => code switch
    {
        1 => "is not configured correctly",
        3 => "may have a damaged driver or too little memory",
        10 => "cannot start",
        12 => "cannot find enough free resources to use",
        14 => "needs a restart before it can work",
        18 => "needs its drivers installed again",
        19 => "has damaged settings in the Windows registry",
        21 => "is being removed by Windows",
        22 => "is switched off",
        24 => "is not present, is not working properly, or has not installed all of its drivers",
        28 => "has no driver installed",
        31 => "is not working properly because Windows cannot load the driver for it",
        32 => "has a driver that is switched off",
        37 => "has a driver that returned an error when it started",
        39 => "has a driver that is damaged or missing",
        40 => "has damaged service information in the Windows registry",
        41 => "was found, but Windows cannot load its driver",
        43 => "was stopped by Windows because it reported a problem",
        48 => "was blocked from starting because its driver is known to have problems with Windows",
        52 => "has a driver whose digital signature Windows cannot verify",
        _ => "reports a problem",
    };
}

public static class TempProfiles
{
    private static readonly Regex TempName = new(@"^TEMP(\..+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for C:\Users\TEMP and C:\Users\TEMP.name (the folders Windows makes when the normal profile cannot load).</summary>
    public static bool IsTemporaryPath(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return false;
        const char sep = '\\';
        var p = profilePath.Replace('/', sep).TrimEnd(sep);
        var name = p[(p.LastIndexOf(sep) + 1)..];
        var up = p.LastIndexOf(sep) > 0 ? p[..p.LastIndexOf(sep)] : "";
        var parent = up[(up.LastIndexOf(sep) + 1)..];
        return parent.Equals("Users", StringComparison.OrdinalIgnoreCase) && TempName.IsMatch(name);
    }
}
