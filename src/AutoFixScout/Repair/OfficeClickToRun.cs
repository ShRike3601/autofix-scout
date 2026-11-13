using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>
/// Microsoft Office installed by Click-to-Run (Microsoft 365, Office 2016 and later). Shared with the elevated executor
/// (linked file): the executor reads the platform and language itself and builds the repair arguments from fixed text,
/// so nothing the app passes ever reaches the Office repair command line.
/// </summary>
internal static class OfficeClickToRun
{
    public const string ConfigKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";

    public sealed record Config(string? Platform, string? Version, string? Products, string? Culture);

    /// <summary>The Click-to-Run configuration from the 64-bit registry view, or null when Click-to-Run Office is not installed.</summary>
    public static Config? Read()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(ConfigKey);
        if (k is null) return null;
        return new Config(k.GetValue("Platform") as string, k.GetValue("VersionToReport") as string,
            k.GetValue("ProductReleaseIds") as string, k.GetValue("ClientCulture") as string);
    }

    public static bool IsValidPlatform(string? platform) => platform is "x64" or "x86";

    /// <summary>Office language tags: "en-us", "de-de", also with a script ("sr-latn-rs"). Letters and dashes only.</summary>
    public static bool IsValidCulture(string? culture) =>
        culture is not null && Regex.IsMatch(culture, @"\A[A-Za-z]{2,3}(-[A-Za-z]{4})?-[A-Za-z]{2}\z");

    /// <summary>The fixed Quick Repair arguments, or null when the registry values do not look like Office's own.</summary>
    public static string[]? QuickRepairArgs(string? platform, string? culture) =>
        IsValidPlatform(platform) && IsValidCulture(culture)
            ? new[]
            {
                "scenario=Repair", $"platform={platform}", $"culture={culture!.ToLowerInvariant()}", "forceappshutdown=True",
                "RepairType=QuickRepair", "DisplayLevel=False",
            }
            : null;

    /// <summary>
    /// "C:\Program Files\Common Files\Microsoft Shared\ClickToRun\OfficeClickToRun.exe". The Common Files folder comes from
    /// the machine registry (64-bit view), never from an environment variable a standard user could set.
    /// </summary>
    public static string ExePath
    {
        get
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
            var common = k?.GetValue("CommonFilesDir") as string;
            if (string.IsNullOrEmpty(common) || !Path.IsPathFullyQualified(common))
                common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            return Path.Combine(common, "Microsoft Shared", "ClickToRun", "OfficeClickToRun.exe");
        }
    }
}
