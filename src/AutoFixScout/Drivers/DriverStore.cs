using System.Management;

namespace AutoFixScout.Drivers;

/// <summary>A device's current driver (spec 7.1 probe.drivers): IDs and status from WMI, driver from the registry. Read-only.</summary>
internal sealed record DeviceDriver(string InstanceId, IReadOnlyList<string> HardwareIds, int? ProblemCode, string? InfName, string? Version);

/// <summary>A driver package already in this PC's driver store that fits the device.</summary>
internal sealed record StoredDriver(string InfPath, Version Version, string MatchedHardwareId);

/// <summary>
/// Shared by the app (to decide whether rollback is possible) and the elevated executor (to do it),
/// so both always agree. Compiled into both projects.
/// </summary>
internal static class DriverStore
{
    public static string InfDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    public static DeviceDriver? Read(string instanceId)
    {
        if (instanceId.Contains('\'')) return null;
        var wql = instanceId.Replace(@"\", @"\\");
        using var dev = new ManagementObjectSearcher($"SELECT HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID = '{wql}'");
        var entity = dev.Get().Cast<ManagementBaseObject>().FirstOrDefault();
        if (entity is null) return null;
        var (inf, version) = ReadInstalledDriver(instanceId);
        if (inf is null || version is null)
        {
            // Fallback only: Win32_PnPSignedDriver lags real driver changes (test step 5: still the old version 6 s
            // after a successful install), which would make the post-check undo a change that worked.
            using var drv = new ManagementObjectSearcher($"SELECT InfName, DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID = '{wql}'");
            var driver = drv.Get().Cast<ManagementBaseObject>().FirstOrDefault();
            inf ??= driver?["InfName"] as string;
            version ??= driver?["DriverVersion"] as string;
        }
        return new DeviceDriver(
            instanceId,
            entity["HardwareID"] as string[] ?? Array.Empty<string>(),
            entity["ConfigManagerErrorCode"] is uint code ? (int)code : null,
            inf,
            version);
    }

    /// <summary>
    /// The driver Windows has installed on the device right now: Enum\{instance}\Driver points at the device's class key,
    /// where setup writes InfPath and DriverVersion as part of the install. Readable without admin.
    /// </summary>
    internal static (string? InfName, string? Version) ReadInstalledDriver(string instanceId)
    {
        try
        {
            using var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            if (enumKey?.GetValue("Driver") is not string classKey) return (null, null);
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{classKey}");
            return (cls?.GetValue("InfPath") as string, cls?.GetValue("DriverVersion") as string);
        }
        catch (Exception) { return (null, null); }
    }

    /// <summary>
    /// The newest third-party package (oem*.inf) older than the current one, of the same device class,
    /// that names one of this device's hardware IDs. Null means "nothing to roll back to".
    /// </summary>
    public static StoredDriver? FindPrevious(DeviceDriver device, string? infDirectory = null)
    {
        var dir = infDirectory ?? InfDirectory;
        if (device.InfName is null || !Version.TryParse(device.Version, out var current)) return null;
        var currentInf = ReadVersionSection(Path.Combine(dir, device.InfName));
        if (currentInf is null) return null;

        StoredDriver? best = null;
        foreach (var path in Directory.EnumerateFiles(dir, "oem*.inf"))
        {
            if (Path.GetFileName(path).Equals(device.InfName, StringComparison.OrdinalIgnoreCase)) continue;
            var inf = ReadVersionSection(path);
            if (inf is null || !inf.Value.Class.Equals(currentInf.Value.Class, StringComparison.OrdinalIgnoreCase)) continue;
            if (inf.Value.Version >= current || (best != null && inf.Value.Version <= best.Version)) continue;
            // Most specific hardware ID first, so the install targets this exact device.
            var text = File.ReadAllText(path);
            var hwid = device.HardwareIds.FirstOrDefault(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));
            if (hwid != null) best = new StoredDriver(path, inf.Value.Version, hwid);
        }
        return best;
    }

    /// <summary>When this package was added to the store; a stand-in for "driver changed recently".</summary>
    public static DateTime? StagedUtc(string? infName, string? infDirectory = null)
    {
        if (infName is null) return null;
        var path = Path.Combine(infDirectory ?? InfDirectory, infName);
        return File.Exists(path) ? File.GetCreationTimeUtc(path) : null;
    }

    /// <summary>Reads Class and the version part of DriverVer from an INF's [Version] section.</summary>
    internal static (string Class, Version Version)? ReadVersionSection(string path)
    {
        if (!File.Exists(path)) return null;
        string? cls = null;
        Version? ver = null;
        var inVersion = false;
        // ReadAllLines honours the UTF-16 BOM most INFs have.
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split(';')[0].Trim();
            if (line.StartsWith('[')) { if (inVersion) break; inVersion = line.Equals("[Version]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inVersion || !line.Contains('=')) continue;
            var key = line[..line.IndexOf('=')].Trim();
            var value = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
            if (key.Equals("Class", StringComparison.OrdinalIgnoreCase)) cls = value;
            else if (key.Equals("DriverVer", StringComparison.OrdinalIgnoreCase) && value.Split(',') is { Length: 2 } parts)
                Version.TryParse(parts[1].Trim(), out ver);
        }
        return cls != null && ver != null ? (cls, ver) : null;
    }
}
