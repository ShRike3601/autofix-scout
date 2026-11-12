using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>
/// Repairs that only touch the person's own settings, so no admin rights and no permission prompt.
/// Each one saves what it changes to %LOCALAPPDATA%\AutoFixScout\backups first.
/// </summary>
public static class UserRepairs
{
    public static string BackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "backups");

    private static string Backup(string name, string content)
    {
        Directory.CreateDirectory(BackupDir);
        var path = Path.Combine(BackupDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>Proxy off and setup script removed for the current user. Values are saved first as a .reg-style text.</summary>
    public static (bool Ok, string Detail) ResetUserProxy()
    {
        using var k = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: true);
        if (k is null) return (false, "internet settings key not found");
        var before = $"ProxyEnable={k.GetValue("ProxyEnable")}\nProxyServer={k.GetValue("ProxyServer")}\nProxyOverride={k.GetValue("ProxyOverride")}\nAutoConfigURL={k.GetValue("AutoConfigURL")}\n";
        var saved = Backup("proxy", before);
        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        if (k.GetValue("AutoConfigURL") != null) k.DeleteValue("AutoConfigURL", false);
        NotifyInternetSettingsChanged();
        return (true, $"proxy switched off; previous values saved to {saved}");
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    private static void NotifyInternetSettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, 39 /* INTERNET_OPTION_SETTINGS_CHANGED */, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /* INTERNET_OPTION_REFRESH */, IntPtr.Zero, 0);
    }

    /// <summary>Creates the folder TEMP points to. If TEMP itself is unusable, points it back to the Windows default for this user.</summary>
    public static (bool Ok, string Detail) RepairTemp()
    {
        var temp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable("TEMP");
        var expanded = string.IsNullOrWhiteSpace(temp) ? null : Environment.ExpandEnvironmentVariables(temp);
        if (expanded is not null && Path.IsPathFullyQualified(expanded))
        {
            Directory.CreateDirectory(expanded);
            return (true, $"created {expanded}");
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        Directory.CreateDirectory(fallback);
        Backup("temp", $"TEMP={temp}\nTMP={Environment.GetEnvironmentVariable("TMP", EnvironmentVariableTarget.User)}\n");
        Environment.SetEnvironmentVariable("TEMP", @"%USERPROFILE%\AppData\Local\Temp", EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("TMP", @"%USERPROFILE%\AppData\Local\Temp", EnvironmentVariableTarget.User);
        return (true, $"TEMP/TMP set back to the Windows default ({fallback})");
    }

    /// <summary>Package full names: name_version_arch_resourceid_publisherid. Letters, digits, dot, dash, underscore and tilde only.</summary>
    public static bool IsValidPackageFullName(string name) =>
        name.Length is > 0 and < 256 && Regex.IsMatch(name, @"^[A-Za-z0-9.\-_~]+$");

    /// <summary>
    /// Re-registers one installed Store package from its own install folder (the standard repair for a damaged app).
    /// The name is validated, then looked up in the installed packages; it never reaches a command line as text to run.
    /// </summary>
    public static (bool Ok, string Detail) ReregisterApp(string fullName)
    {
        if (!IsValidPackageFullName(fullName)) return (false, "rejected package name");
        // The name is passed as a separate argument ($args[0]) to a fixed script.
        const string Script =
            "$n = $args[0]; $p = Get-AppxPackage | Where-Object { $_.PackageFullName -eq $n }; " +
            "if (-not $p) { Write-Output 'not installed'; exit 3 }; " +
            "Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $p.InstallLocation 'AppxManifest.xml') -ErrorAction Stop; " +
            "$q = Get-AppxPackage | Where-Object { $_.PackageFullName -eq $n }; Write-Output ('status ' + $q.Status); exit 0";
        var (code, output) = RunPowerShell(Script, fullName);
        return (code == 0, $"re-register {fullName}: exit {code} {output.Trim()}");
    }

    public static (int Code, string Output) RunPowerShell(string script, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", "& { " + script + " }" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } return (1, "timed out"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }


    /// <summary>Ends the Start menu, search and shell-experience processes; Windows starts them again on demand.</summary>
    public static (bool Ok, string Detail) RestartStartMenu()
    {
        var ended = 0;
        foreach (var name in new[] { "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(5000); ended++; } catch (Exception) { }
                p.Dispose();
            }
        Thread.Sleep(3000);
        return (true, $"shell processes restarted: {ended}");
    }
    /// <summary>Ends and restarts the taskbar/File Explorer process. Open programs are not affected.</summary>
    public static (bool Ok, string Detail) RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); p.WaitForExit(10_000); } catch (Exception) { }
            p.Dispose();
        }
        Thread.Sleep(1500);
        // Windows usually restarts the shell by itself; start it only if it did not.
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
        Thread.Sleep(2000);
        return (Process.GetProcessesByName("explorer").Length > 0, "explorer restarted");
    }

    /// <summary>Deletes the icon and thumbnail cache files (Windows rebuilds them), then restarts Explorer so they are rebuilt.</summary>
    public static (bool Ok, string Detail) RebuildIconCache()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        var files = new List<string> { Path.Combine(local, "IconCache.db") };
        if (Directory.Exists(dir))
            files.AddRange(Directory.GetFiles(dir, "iconcache_*.db").Concat(Directory.GetFiles(dir, "thumbcache_*.db")));
        foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); p.WaitForExit(10_000); } catch (Exception) { } p.Dispose(); }
        var deleted = 0;
        foreach (var f in files)
        {
            try { if (File.Exists(f)) { File.Delete(f); deleted++; } }
            catch (Exception) { } // in use by another process: Windows keeps using it, nothing breaks
        }
        Thread.Sleep(1000);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
        return (true, $"icon/thumbnail cache files removed: {deleted}");
    }

    // ---- Microsoft Defender (works without admin rights; checked on the test bench) ----

    /// <summary>Defender's command-line tool at its fixed location (64-bit Program Files, also from a 32-bit process).</summary>
    public static string MpCmdRunPath => Path.Combine(
        Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Windows Defender", "MpCmdRun.exe");

    private static (int Code, string Output) MpCmdRun(int timeoutMinutes, params string[] args)
    {
        if (!File.Exists(MpCmdRunPath)) return (-1, "MpCmdRun.exe not found");
        var psi = new ProcessStartInfo(MpCmdRunPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes))) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, (stdout.Result + stderr.Result).Replace("\0", "").Trim());
    }

    /// <summary>security.update_signatures: MpCmdRun -SignatureUpdate (fixed arguments).</summary>
    public static (bool Ok, string Detail) UpdateDefenderSignatures()
    {
        var (code, output) = MpCmdRun(15, "-SignatureUpdate");
        return (code == 0, $"MpCmdRun -SignatureUpdate exit {code}: {output}");
    }

    /// <summary>security.quick_scan: MpCmdRun -Scan -ScanType 1 (quick scan). Exit 2 = threats found; Defender handles them itself.</summary>
    public static (bool Ok, string Detail) DefenderQuickScan()
    {
        var (code, output) = MpCmdRun(60, "-Scan", "-ScanType", "1");
        var verdict = code switch { 0 => "no threats found", 2 => "threats found and handed to Microsoft Defender", _ => "scan failed" };
        return (code is 0 or 2, $"MpCmdRun quick scan exit {code} ({verdict}): {output}");
    }
}
