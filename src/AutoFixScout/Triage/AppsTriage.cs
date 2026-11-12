using AutoFixScout.Probes;
using AutoFixScout.Repair;

namespace AutoFixScout.Triage;

/// <summary>Which of the area's problems the person described. All = not said; every part is checked.</summary>
public enum AppsFocus { All, Store, Office, OneDrive }

/// <summary>Microsoft Office installed by Click-to-Run (null in the state when it is not).</summary>
public sealed record OfficeInstall(string Platform, string Version, string Products);

/// <summary>OneDrive as found on this computer. The install path stays out of the state (it names the user).</summary>
public sealed record OneDriveInstall(bool PerUser, bool Running);

/// <summary>probe.apps: Store packages and services, the clock, Click-to-Run Office and OneDrive.</summary>
public sealed class AppsState
{
    public AppsFocus Focus { get; set; } = AppsFocus.All;
    public IReadOnlyList<ShellPackage> StorePackages { get; set; } = new List<ShellPackage>();
    /// <summary>False when the package list could not be read, so a missing Store is not reported by mistake.</summary>
    public bool StorePackagesRead { get; set; }
    public IReadOnlyList<UpdateServiceInfo> Services { get; set; } = new List<UpdateServiceInfo>();
    public double? ClockOffsetSeconds { get; set; }
    public OfficeInstall? Office { get; set; }
    public OneDriveInstall? OneDrive { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

/// <summary>
/// "The Microsoft Store, Office or OneDrive does not work". Such problems rarely leave a clear trace, so the steps are
/// offered by symptom, mildest first. Store: clear the Store cache, register the Store packages again, switch needed
/// services back on, then the Windows troubleshooter. Office: Quick Repair, then Online Repair by the person.
/// OneDrive: start it, then reset it.
/// </summary>
public static class AppsTriage
{
    /// <summary>Store, Store purchases and App Installer (Get-AppxPackage Name).</summary>
    public static readonly string[] StorePackageNames = { "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller" };

    /// <summary>Services the Store needs to install and update apps. All are in ServiceDefaults.</summary>
    public static readonly string[] StoreServices = { "InstallService", "ClipSVC", "AppXSvc", "wuauserv", "BITS" };

    public const string OfficeService = "ClickToRunSvc";

    public static IEnumerable<string> ServiceNames => StoreServices.Append(OfficeService);

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private static bool Store(AppsState s) => s.Focus is AppsFocus.All or AppsFocus.Store;
    private static bool Office(AppsState s) => s.Focus is AppsFocus.All or AppsFocus.Office;
    private static bool OneDrive(AppsState s) => s.Focus is AppsFocus.All or AppsFocus.OneDrive;

    private static bool IsOk(ShellPackage p) => p.Status.Equals("Ok", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<UpdateServiceInfo> Faulty(AppsState s, IEnumerable<string> names) =>
        s.Services.Where(x => names.Contains(x.Name, StringComparer.OrdinalIgnoreCase) && ServiceDefaults.IsFault(x.Name, x.Status, x.StartType));

    private static string DisplayName(string service) => ServiceDefaults.Find(service)?.DisplayName ?? service;

    private static bool ClockWrong(AppsState s) => s.ClockOffsetSeconds is { } off && Math.Abs(off) > HealthTriage.ClockTolerSeconds;

    public static IReadOnlyList<Finding> Evaluate(AppsState s)
    {
        if (s.ProbeErrors.Count > 0 && !s.StorePackagesRead && s.Services.Count == 0 && s.Office is null && s.OneDrive is null)
            return new[] { F(FindingCode.ProbeIncomplete, "AutoFix Scout could not complete the check.", string.Join("; ", s.ProbeErrors)) };
        var f = new List<Finding>();
        if (Store(s))
        {
            if (s.StorePackagesRead && !s.StorePackages.Any(p => p.Name == "Microsoft.WindowsStore"))
                f.Add(F(FindingCode.StoreMissing, "The Microsoft Store is not installed for your Windows user account.", "Microsoft.WindowsStore not found"));
            if (s.StorePackages.FirstOrDefault(p => !IsOk(p)) is { } broken)
                f.Add(F(FindingCode.StorePackageDamaged, "A part of the Microsoft Store is damaged.", $"{broken.FullName}: {broken.Status}"));
            foreach (var x in Faulty(s, StoreServices))
                f.Add(F(FindingCode.HealthServiceOff,
                    $"The Windows service \"{DisplayName(x.Name)}\" is switched off. The Microsoft Store needs it to install and update apps.",
                    $"{x.Name} {x.Status}/{x.StartType}"));
        }
        if (Office(s) && s.Office is not null)
            foreach (var x in Faulty(s, new[] { OfficeService }))
                f.Add(F(FindingCode.HealthServiceOff,
                    $"The Windows service \"{DisplayName(x.Name)}\" is switched off. Microsoft Office needs it to start and to update.",
                    $"{x.Name} {x.Status}/{x.StartType}"));
        if (ClockWrong(s))
            f.Add(F(FindingCode.HealthClockWrong,
                "The computer's clock is wrong. A wrong clock causes the Microsoft Store, sign-in and updates to fail.",
                $"clock offset {s.ClockOffsetSeconds:0} s"));
        if (s.Focus == AppsFocus.Office && s.Office is null)
            f.Add(F(FindingCode.OfficeNotFound,
                "AutoFix Scout did not find a Microsoft 365 or Microsoft Office installation that it can repair automatically.",
                "no Click-to-Run configuration"));
        if (s.Focus == AppsFocus.OneDrive)
        {
            if (s.OneDrive is null)
                f.Add(F(FindingCode.OneDriveNotFound, "OneDrive is not installed for your Windows user account.", "OneDrive.exe not found"));
            else if (!s.OneDrive.Running)
                f.Add(F(FindingCode.OneDriveNotRunning, "OneDrive is not running, so your files are not being synchronised.", "OneDrive.exe not running"));
        }
        if (f.Count > 0) return f;
        var what = s.Focus switch
        {
            AppsFocus.Store => "the Microsoft Store",
            AppsFocus.Office => "Microsoft Office",
            AppsFocus.OneDrive => "OneDrive",
            _ => "the Microsoft Store, Microsoft Office or OneDrive",
        };
        return new[] { F(FindingCode.LooksNormal,
            $"Windows has not recorded a fault in {what}. AutoFix Scout can still repair it step by step.",
            $"store packages: {string.Join(", ", s.StorePackages.Select(p => $"{p.Name}:{p.Status}"))}; " +
            $"office: {(s.Office is { } o ? $"{o.Platform} {o.Version}" : "none")}; onedrive: {(s.OneDrive is { } d ? (d.Running ? "running" : "not running") : "none")}") };
    }

    public static IReadOnlyList<Rung> Ladder(AppsState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var r = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return r;
        if (ClockWrong(s))
            r.Add(new("sync_clock", "time.resync", 1,
                "AutoFix Scout can set the clock from Microsoft's internet time server. Windows will ask for your permission.", "system"));
        if (Store(s))
        {
            if (s.StorePackages.Any(p => p.Name == "Microsoft.WindowsStore"))
                r.Add(new("reset_store_cache", "apps.reset_store_cache", 0,
                    "AutoFix Scout can clear the stored data of the Microsoft Store. The Store opens by itself when this is finished; your apps are not changed."));
            foreach (var p in s.StorePackages.OrderBy(p => IsOk(p) ? 1 : 0).Take(3))
                r.Add(new($"reregister_{p.Name.ToLowerInvariant()}", "apps.reregister", 0,
                    $"AutoFix Scout can register the Windows component \"{Friendly(p.Name)}\" again, which repairs many problems with downloading and updating apps.",
                    p.FullName));
            foreach (var x in Faulty(s, StoreServices))
                r.Add(new($"restore_{x.Name.ToLowerInvariant()}", "service.restore_default", 1,
                    $"AutoFix Scout can set the \"{DisplayName(x.Name)}\" service back to the way Windows normally runs it. Windows will ask for your permission.",
                    x.Name));
            r.Add(new("store_troubleshooter", "ui.open_settings_page", 0,
                "Please open \"Other troubleshooters\" in the window that opens and select \"Run\" next to \"Windows Store Apps\". " +
                "Follow the steps it shows, then return to AutoFix Scout.", "troubleshoot"));
        }
        if (Office(s))
        {
            if (s.Office is not null)
            {
                foreach (var x in Faulty(s, new[] { OfficeService }))
                    r.Add(new("restore_clicktorunsvc", "service.restore_default", 1,
                        $"AutoFix Scout can set the \"{DisplayName(x.Name)}\" service back to the way Windows normally runs it. Windows will ask for your permission.",
                        x.Name));
                r.Add(new("office_quick_repair", "office.quick_repair", 1,
                    "AutoFix Scout can run the Office \"Quick Repair\", which fixes most problems with Word, Excel and Outlook without changing your documents. " +
                    "Open Office programs will close, so please save your work first. This can take 5 to 10 minutes. Windows will ask for your permission.",
                    "system"));
            }
            if (s.Office is not null || s.Focus == AppsFocus.Office)
                r.Add(new("office_online_repair", "ui.open_settings_page", 0,
                    "Please find Microsoft 365 or Microsoft Office in the list of \"Installed apps\", select the three dots next to it and then \"Modify\". " +
                    "Choose \"Online Repair\" if it is offered, otherwise \"Repair\". An online repair needs an internet connection and can take 30 minutes or more.",
                    "apps"));
        }
        if (OneDrive(s) && s.OneDrive is { } od)
        {
            if (!od.Running)
                r.Add(new("start_onedrive", "apps.start_onedrive", 0,
                    "AutoFix Scout can start OneDrive. If OneDrive asks you to sign in, please sign in with your Microsoft account."));
            r.Add(new("reset_onedrive", "apps.reset_onedrive", 1,
                "AutoFix Scout can reset OneDrive, which repairs most synchronisation problems. Your files are kept, both on this computer and online. " +
                "OneDrive will then compare all your files with the cloud again, which can take a long time when there are many files."));
        }
        return r;
    }

    public static string Friendly(string package) => package switch
    {
        "Microsoft.WindowsStore" => "Microsoft Store",
        "Microsoft.StorePurchaseApp" => "Store purchases",
        "Microsoft.DesktopAppInstaller" => "App Installer",
        _ => package,
    };

    public static string? NotFixedHint(IReadOnlyList<Finding> after, AppsFocus focus = AppsFocus.All)
    {
        const string Store =
            "If the Microsoft Store still does not work, signing out of the Store and signing in again often helps. " +
            "The person helping you can also try a new Windows user account, because the problem can be limited to one account.";
        const string Office =
            "If Microsoft Office still does not work, the person helping you can uninstall it and install it again from the Microsoft account page where it was bought.";
        const string OneDrive =
            "If OneDrive still does not synchronise, please check that you are signed in and that your OneDrive storage is not full. " +
            "The person helping you can also install OneDrive again from Microsoft's web site.";
        return focus switch
        {
            AppsFocus.Store => Store,
            AppsFocus.Office => Office,
            AppsFocus.OneDrive => OneDrive,
            _ => Store,
        };
    }
}
