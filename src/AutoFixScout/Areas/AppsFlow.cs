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
        var errors = new List<string>();
        try
        {
            s.StorePackages = ReadPackages();
            s.StorePackagesRead = true;
        }
        catch (Exception ex) { errors.Add("store packages: " + ex.Message); }
        try { s.Services = AppsTriage.ServiceNames.Select(ReadService).ToList(); }
        catch (Exception ex) { errors.Add("services: " + ex.Message); }
        // No internet is common when the Store fails; an unread clock is simply not judged.
        try { s.ClockOffsetSeconds = ReadClockOffset(); }
        catch (Exception) { }
        try
        {
            if (OfficeClickToRun.Read() is { } c)
                s.Office = new OfficeInstall(c.Platform ?? "", c.Version ?? "", c.Products ?? "");
        }
        catch (Exception ex) { errors.Add("office: " + ex.Message); }
        try
        {
            if (AppRepairs.FindOneDrive() is { } od)
                s.OneDrive = new OneDriveInstall(AppRepairs.IsPerUser(od), AppRepairs.OneDriveRunning());
        }
        catch (Exception ex) { errors.Add("onedrive: " + ex.Message); }
        s.ProbeErrors = errors;
        return s;
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
