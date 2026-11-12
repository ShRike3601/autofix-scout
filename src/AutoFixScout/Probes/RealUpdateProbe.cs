using System.Globalization;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.update: Windows Update services, a waiting restart, the last successful install and system drive space.
/// Read-only, no admin, never throws. Does not search for updates (that can take minutes and changes the history).
/// </summary>
public sealed class RealUpdateProbe : IUpdateProbe
{
    public static readonly string[] ServiceNames = { "wuauserv", "BITS", "CryptSvc" };

    private static readonly string[] RebootKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
    };

    public UpdateState Read()
    {
        var errors = new List<string>();
        var s = new UpdateState { ProbeErrors = errors };
        s.Services = ServiceNames.Select(ReadService).ToList();
        if (s.Services.All(x => x.Status == "Unknown")) errors.Add("services: none could be read");
        s.RebootPending = ReadRebootPending();
        s.LastInstallUtc = ReadLastInstall();
        try { (_, s.FreeBytes, s.TotalBytes) = RealStorageProbe.ReadSystemDrive(); }
        catch (Exception) { /* unknown */ }
        return s;
    }

    private static UpdateServiceInfo ReadService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return new UpdateServiceInfo(name, sc.Status.ToString(), sc.StartType switch
            {
                ServiceStartMode.Automatic => "Automatic",
                ServiceStartMode.Manual => "Manual",
                ServiceStartMode.Disabled => "Disabled",
                _ => "Unknown",
            });
        }
        catch (InvalidOperationException) { return new UpdateServiceInfo(name, "NotFound", "NotFound"); }
        catch (Exception) { return new UpdateServiceInfo(name, "Unknown", "Unknown"); }
    }

    private static Tri ReadRebootPending()
    {
        try
        {
            foreach (var path in RebootKeys)
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key != null) return Tri.Yes;
            }
            return Tri.No;
        }
        catch (Exception) { return Tri.Unknown; }
    }

    /// <summary>The Windows Update agent's own record first, then the newest installed update Windows lists; null if neither answers.</summary>
    private static DateTime? ReadLastInstall()
    {
        try
        {
            dynamic au = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", throwOnError: true)!)!;
            object? value = au.Results.LastInstallationSuccessDate;
            if (value is DateTime d && d.Year > 2000) return DateTime.SpecifyKind(d, DateTimeKind.Utc);
        }
        catch (Exception) { /* fall back */ }
        try
        {
            using var q = new ManagementObjectSearcher("SELECT InstalledOn FROM Win32_QuickFixEngineering");
            DateTime? newest = null;
            foreach (var h in q.Get())
            {
                if (DateTime.TryParse(h["InstalledOn"] as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var on)
                    && (newest is null || on > newest)) newest = on.ToUniversalTime();
            }
            return newest;
        }
        catch (Exception) { return null; }
    }
}
