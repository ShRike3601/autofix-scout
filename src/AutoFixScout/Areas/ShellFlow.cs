using System.ServiceProcess;
using System.Text.Json;
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

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        var s = Read();
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
            var h = new RealHealthProbe().Read();
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
        try { s.Packages = ReadPackages(); }
        catch (Exception ex) { errors.Add("shell packages: " + ex.Message); }
        s.ProbeErrors = errors;
        return s;
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
