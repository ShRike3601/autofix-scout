using System.ServiceProcess;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"Apps and the Microsoft Store": the Store, Microsoft Office and OneDrive. Real test: the person's answer.</summary>
public static class AppsFlow
{
    public const string Topic = "apps";
    public const string What = "Apps and the Microsoft Store";
    public const string TestQuestion = "Do the Microsoft Store and your apps work now?";

    /// <summary>The real-test question for the part the person described.</summary>
    public static string TestQuestionFor(AppsFocus focus) => focus switch
    {
        AppsFocus.Store => "Does the Microsoft Store open, and do your apps download and update now?",
        AppsFocus.Office => "Do Word, Excel and Outlook open and work now?",
        AppsFocus.OneDrive => "Does OneDrive show that your files are up to date now?",
        AppsFocus.Install => "Does the program install now?",
        _ => TestQuestion,
    };

    public static Task<Diagnosis> DiagnoseAsync(AppsFocus focus = AppsFocus.All) => Task.Run(() =>
    {
        var s = Read(focus);
        var f = AppsTriage.Evaluate(s);
        return new Diagnosis(s, f, AppsTriage.Ladder(s, f), false, AppsTriage.NotFixedHint(f, focus));
    });

    /// <summary>Read-only: Store packages, the services they need, the clock, Click-to-Run Office and OneDrive.</summary>
    public static AppsState Read(AppsFocus focus = AppsFocus.All)
    {
        var s = new AppsState { Focus = focus };
        // The parts read separate fields, so they run side by side (the Store package list and the clock check over the
        // network are the slow ones). Errors keep this order.
        var parts = new List<(string? Name, Action Read)>
        {
            ("store packages", () => { s.StorePackages = ReadPackages(); s.StorePackagesRead = true; }),
            ("services", () => s.Services = AppsTriage.ServiceNames.Select(ReadService).ToList()),
            // No internet is common when the Store fails; an unread clock is simply not judged.
            (null, () => s.ClockOffsetSeconds = ReadClockOffset()),
            ("office", () =>
            {
                if (OfficeClickToRun.Read() is { } c)
                    s.Office = new OfficeInstall(c.Platform ?? "", c.Version ?? "", c.Products ?? "");
            }),
            ("onedrive", () =>
            {
                if (AppRepairs.FindOneDrive() is { } od)
                    s.OneDrive = new OneDriveInstall(AppRepairs.IsPerUser(od), AppRepairs.OneDriveRunning());
            }),
        };
        if (focus == AppsFocus.Install) parts.Add(("installer", () => s.Install = ReadInstall()));
        var failures = new string?[parts.Count];
        Task.WaitAll(parts.Select((p, i) => Task.Run(() =>
        {
            try { p.Read(); }
            catch (Exception ex) { if (p.Name != null) failures[i] = p.Name + ": " + ex.Message; }
        })).ToArray());
        s.ProbeErrors = failures.Where(e => e != null).Select(e => e!).ToList();
        return s;
    }

    private static InstallState ReadInstall()
    {
        var i = new InstallState { Service = ReadService(AppsTriage.InstallerService) };
        i.Events = ReadInstallEvents();
        i.RestartPending = RestartPending();
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (!string.IsNullOrEmpty(root)) i.SystemDriveFreeBytes = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception) { }
        return i;
    }

    private static readonly System.Text.RegularExpressions.Regex ProductInMessage = new(@"Product:\s*(.+?)\s*--", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Events 11708, 1033 and 1034 of the last 7 days: product names and status codes only.</summary>
    private static IReadOnlyList<InstallEvent> ReadInstallEvents()
    {
        var list = new List<InstallEvent>();
        const string Filter = "*[System[Provider[@Name='MsiInstaller'] and (EventID=11708 or EventID=1033 or EventID=1034) and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                new System.Diagnostics.Eventing.Reader.EventLogQuery("Application", System.Diagnostics.Eventing.Reader.PathType.LogName, Filter));
            for (var n = 0; n < 500 && reader.ReadEvent() is { } e; n++)
                using (e)
                {
                    var id = e.Id;
                    var p0 = e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() ?? "" : "";
                    var product = p0;
                    var code = 0;
                    if (id == 11708) product = ProductInMessage.Match(p0) is { Success: true } m ? m.Groups[1].Value : "";
                    else if (e.Properties.Count > 3) _ = int.TryParse(e.Properties[3].Value?.ToString(), out code);
                    list.Add(new InstallEvent(e.TimeCreated ?? DateTime.MinValue, id, product.Trim(), code));
                }
        }
        catch (System.Diagnostics.Eventing.Reader.EventLogNotFoundException) { }
        return list.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>Pending restart: update flags, or PendingFileRenameOperations (readable without administrator rights).</summary>
    private static bool RestartPending()
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending" })
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (k != null) return true;
        }
        using var sm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
    }

    private static IReadOnlyList<ShellPackage> ReadPackages()
    {
        var names = string.Join(",", AppsTriage.StorePackageNames.Select(n => $"'{n}'"));
        var (code, output) = UserRepairs.RunPowerShell(
            $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; $n = @({names}); @(Get-AppxPackage | Where-Object {{ $n -contains $_.Name }} | " +
            "Select-Object Name,PackageFullName,@{n='Status';e={[string]$_.Status}}) | ConvertTo-Json -Compress");
        if (code != 0) throw new InvalidOperationException($"Get-AppxPackage exit {code}");
        return ShellFlow.ParsePackages(output);
    }

    private static UpdateServiceInfo ReadService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return new UpdateServiceInfo(name, sc.Status == ServiceControllerStatus.Running ? "Running" : sc.Status == ServiceControllerStatus.Stopped ? "Stopped" : sc.Status.ToString(), sc.StartType.ToString());
        }
        catch (InvalidOperationException) { return new UpdateServiceInfo(name, "NotFound", "NotFound"); }
    }

    /// <summary>Same check as the health probe: the Date header of Microsoft's plain-HTTP connectivity test.</summary>
    private static double? ReadClockOffset()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var sent = DateTime.UtcNow;
        using var r = http.Send(new HttpRequestMessage(HttpMethod.Head, "http://www.msftconnecttest.com/connecttest.txt"));
        var received = DateTime.UtcNow;
        if (r.Headers.Date is not { } server) return null;
        return (sent + (received - sent) / 2 - server.UtcDateTime).TotalSeconds;
    }
}
