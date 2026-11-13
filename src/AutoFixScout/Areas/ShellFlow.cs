using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"The Start menu, taskbar or search does not work". Real test: the person's answer.</summary>
public static class ShellFlow
{
    public const string Topic = "shell";
    public const string What = "Start menu and taskbar";
    public const string TestQuestion = "Do the Start menu, the taskbar and search work now?";

    public const string NotificationTestQuestion = "Do notifications appear again?";

    /// <summary>Kept as its own overload so the UI can still pass it as a method group.</summary>
    public static Task<Diagnosis> DiagnoseAsync() => DiagnoseAsync(false);

    public static Task<Diagnosis> DiagnoseAsync(bool notificationFocus) => Task.Run(() =>
    {
        var s = Read();
        s.NotificationFocus = notificationFocus;
        var f = ShellTriage.Evaluate(s);
        return new Diagnosis(s, f, ShellTriage.Ladder(s, f), false, ShellTriage.NotFixedHint(f));
    });

    /// <summary>Read-only: shell crashes from the health probe's event reading, WSearch, and the shell packages.</summary>
    public static ShellState Read()
    {
        var s = new ShellState();
        var errors = new List<string>();
        try
        {
            var h = RealHealthProbe.ReadEventLogs();
            s.ShellCrashes = HealthTriage.ShellCrashes(h);
            errors.AddRange(h.ProbeErrors.Where(e => e.StartsWith("event log", StringComparison.Ordinal)));
        }
        catch (Exception ex) { errors.Add("event log: " + ex.Message); }
        try
        {
            using var sc = new ServiceController("WSearch");
            s.Search = new UpdateServiceInfo("WSearch", sc.Status == ServiceControllerStatus.Running ? "Running" : sc.Status == ServiceControllerStatus.Stopped ? "Stopped" : sc.Status.ToString(), sc.StartType.ToString());
        }
        catch (Exception ex) { errors.Add("search service: " + ex.Message); }
        try { ReadNotifications(s); }
        catch (Exception ex) { errors.Add("notifications: " + ex.Message); }
        try { s.Packages = ReadPackages(); }
        catch (Exception ex) { errors.Add("shell packages: " + ex.Message); }
        s.ProbeErrors = errors;
        return s;
    }

    /// <summary>Read-only: the global toggle, Do not disturb and the per-app choices of the current user.</summary>
    public static void ReadNotifications(ShellState s)
    {
        using var push = Registry.CurrentUser.OpenSubKey(NotificationRepairs.PushKey);
        using var settings = Registry.CurrentUser.OpenSubKey(NotificationRepairs.SettingsKey);
        var apps = new List<(string Id, int? Enabled, int? ShowBanner)>();
        if (settings is not null)
            foreach (var name in settings.GetSubKeyNames())
            {
                using var k = settings.OpenSubKey(name);
                apps.Add((name, k?.GetValue("Enabled") as int?, k?.GetValue("ShowBanner") as int?));
            }
        ApplyNotifications(s, push?.GetValue("ToastEnabled") as int?, settings?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") as int?, apps);
    }

    public static void ApplyNotifications(ShellState s, int? toastEnabled, int? globalToasts, IEnumerable<(string Id, int? Enabled, int? ShowBanner)> apps)
    {
        s.ToastEnabled = toastEnabled;
        s.DoNotDisturb = globalToasts == 0;
        s.AppsNotificationsOff = apps.Where(a => a.Enabled == 0 || a.ShowBanner == 0).Select(a => ShellTriage.NotificationAppName(a.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<ShellPackage> ReadPackages()
    {
        var names = string.Join(",", ShellTriage.PackageNames.Select(n => $"'{n}'"));
        var (code, output) = UserRepairs.RunPowerShell(
            $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; $n = @({names}); @(Get-AppxPackage | Where-Object {{ $n -contains $_.Name }} | " +
            "Select-Object Name,PackageFullName,@{n='Status';e={[string]$_.Status}}) | ConvertTo-Json -Compress");
        if (code != 0) throw new InvalidOperationException($"Get-AppxPackage exit {code}");
        return ParsePackages(output);
    }

    public static IReadOnlyList<ShellPackage> ParsePackages(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return Array.Empty<ShellPackage>();
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        return items.Select(i => new ShellPackage(i.GetProperty("Name").GetString() ?? "", i.GetProperty("PackageFullName").GetString() ?? "",
                i.GetProperty("Status").GetString() ?? ""))
            .Where(p => p.FullName.Length > 0).ToList();
    }
}
