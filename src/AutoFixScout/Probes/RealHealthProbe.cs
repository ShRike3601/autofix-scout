using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net.Http;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using AutoFixScout.Repair;
using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>
/// Read-only (T0). Every part is read separately: a part that fails is named in ProbeErrors and the others still count.
/// Nothing here needs admin rights.
/// </summary>
public sealed class RealHealthProbe : IHealthProbe
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public HealthState Read()
    {
        var s = new HealthState();
        var errors = new List<string>();
        void Part(string name, Action read)
        {
            try { read(); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }

        Part("event log (programs)", () => ReadAppEvents(s));
        Part("event log (system)", () => ReadSystemEvents(s));
        Part("clock", () => s.ClockOffsetSeconds = ReadClockOffset());
        Part("clock settings", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
            s.AutoTimeOff = string.Equals(k?.GetValue("Type") as string, "NoSync", StringComparison.OrdinalIgnoreCase);
        });
        Part("proxy", () => ReadUserProxy(s));
        Part("system proxy", () => ReadWinHttpProxy(s));
        Part("hosts file", () => s.HostsHijacks = HostsFile.Suspicious(File.ReadAllLines(HostsPath)).Select(e => new HostsHijack(e.Host, e.Ip)).ToList());
        Part("network add-ons", () => s.ThirdPartyWinsock = ReadWinsock());
        Part("system PATH", () => s.MissingPathEntries = SystemPath.Missing(ReadMachinePath(), Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Part("temporary folder", () => s.TempFolderMissing = !Directory.Exists(Environment.GetEnvironmentVariable("TEMP") ?? ""));
        Part("services", () => s.Services = ServiceDefaults.All.Select(d => ReadService(d.Name)).ToList());
        Part("Store apps", () => s.BrokenStoreApps = ReadBrokenStoreApps());
        Part("restart flag", () => s.RebootPending = ReadRebootPending());
        Part("drives", () => s.Drives = ReadDrives());
        Part("device errors", () => s.DeviceProblems = ReadDeviceProblems());
        Part("user profile", () => ReadProfile(s));
        Part("recent changes", () => ReadRecentChanges(s));
        Part("restore points", () => ReadRestorePoints(s));
        Part("graphics resets", () => ReadGraphicsResets(s));

        s.ProbeErrors = errors;
        return s;
    }

    /// <summary>Only the proxy settings and the hosts file, read the same way as in <see cref="Read"/> (used by the security check).</summary>
    public static HealthState ReadHostsAndProxy()
    {
        var s = new HealthState();
        var errors = new List<string>();
        void Part(string name, Action read)
        {
            try { read(); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }
        Part("proxy", () => ReadUserProxy(s));
        Part("system proxy", () => ReadWinHttpProxy(s));
        Part("hosts file", () => s.HostsHijacks = HostsFile.Suspicious(File.ReadAllLines(HostsPath)).Select(e => new HostsHijack(e.Host, e.Ip)).ToList());
        s.ProbeErrors = errors;
        return s;
    }

    public static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    // ---- event logs ----

    private static string Since => $"TimeCreated[timediff(@SystemTime) <= {(long)Window.TotalMilliseconds}]";

    private static IEnumerable<EventRecord> Query(string log, string filter, int max = 2000)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, $"*[System[{filter} and {Since}]]"));
        for (var i = 0; i < max && reader.ReadEvent() is { } e; i++)
            using (e) yield return e;
    }

    private static string? Prop(EventRecord e, int i) => e.Properties.Count > i ? e.Properties[i].Value?.ToString() : null;

    private static void ReadAppEvents(HealthState s)
    {
        var crashes = new List<(string App, string? Module, string? Code, DateTime When, string? Path)>();
        foreach (var e in Query("Application", "Provider[@Name='Application Error'] and EventID=1000"))
        {
            var code = Prop(e, 6); // hex text, for example "c0000005"
            crashes.Add((Prop(e, 0) ?? "unknown", Prop(e, 3), code?.Replace("0x", "", StringComparison.OrdinalIgnoreCase), e.TimeCreated?.ToUniversalTime() ?? DateTime.MinValue, Prop(e, 10)));
        }
        s.Crashes = crashes
            .GroupBy(c => (c.App.ToLowerInvariant(), (c.Module ?? "").ToLowerInvariant()))
            .Select(g => new CrashInfo(g.First().App, g.First().Module, g.First().Code, g.Count(), g.Max(x => x.When), g.First().Path))
            .OrderByDescending(c => c.Count).ThenByDescending(c => c.LastUtc).ToList();

        s.Hangs = Query("Application", "Provider[@Name='Application Hang'] and EventID=1002")
            .Select(e => Prop(e, 0) ?? "unknown").GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        s.RuntimeErrors = Query("Application", "Provider[@Name='SideBySide'] and Level=2")
            .Select(e => e.Properties.Select(p => p.Value?.ToString() ?? "").FirstOrDefault(v => v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                                                           || v.Contains(".exe", StringComparison.OrdinalIgnoreCase)) ?? "a program")
            .Select(v => Path.GetFileName(v.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.Contains(".exe", StringComparison.OrdinalIgnoreCase)) ?? v))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }

    private static readonly HashSet<string> TransientUpdateErrors = new() { "0x80240016", "0x8024000b", "0x80073d02", "0x80240022" };

    /// <summary>The event stores the HRESULT as a signed number (-2147009278) or text; returns "0x80073d02".</summary>
    public static string HexCode(string? value) =>
        int.TryParse(value, out var n) ? $"0x{unchecked((uint)n):x8}" : (value ?? "").Trim().ToLowerInvariant();

    private static void ReadSystemEvents(HealthState s)
    {
        s.BlueScreens = Query("System", "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001").Count();
        s.UnexpectedShutdowns = Query("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41").Count();
        s.DiskErrors = Query("System", "(Level=1 or Level=2 or Level=3) and ((Provider[@Name='disk'] and (EventID=7 or EventID=51 or EventID=153)) or (Provider[@Name='Ntfs'] and (EventID=55 or EventID=98)) or (Provider[@Name='Microsoft-Windows-Ntfs'] and (EventID=55 or EventID=98)))").Count();
        s.HardwareErrors = Query("System", "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2)").Count();
        // Transient codes ("another installation in progress", "cancelled", "app in use") are not failures.
        s.UpdateFailures = Query("System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=20")
            .Count(e => !TransientUpdateErrors.Contains(HexCode(Prop(e, 0))));
        s.FailingServices = Query("System", "Provider[@Name='Service Control Manager'] and (EventID=7000 or EventID=7009 or EventID=7023 or EventID=7031 or EventID=7034)")
            .Select(e => Prop(e, 0) ?? "").Where(n => n.Length > 0)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(8).ToList();
    }



    // ---- recent changes and graphics resets ----

    public static readonly TimeSpan ChangeWindow = TimeSpan.FromDays(7);

    private static IEnumerable<EventRecord> QueryWindow(string log, string filter, TimeSpan window, int max = 500)
    {
        var since = $"TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]";
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, $"*[System[{filter} and {since}]]"));
        for (var i = 0; i < max && reader.ReadEvent() is { } e; i++)
            using (e) yield return e;
    }

    private static void ReadRecentChanges(HealthState s)
    {
        var list = new List<RecentChange>();
        var cutoff = DateTime.UtcNow - ChangeWindow;
        // Programs: the Uninstall keys (machine, 32-bit on 64-bit Windows, current user).
        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(Uninstall);
            if (k is null) continue;
            foreach (var name in k.GetSubKeyNames())
            {
                using var e = k.OpenSubKey(name);
                if (e is null) continue;
                if (e.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) continue;
                if (e.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (ParseInstallDate(e.GetValue("InstallDate")?.ToString()) is { } when && when >= cutoff.Date)
                    list.Add(new RecentChange(RecentChangeKind.Program, display.Trim(), when));
            }
        }
        // Windows updates: "Installation Successful" (event 19). Antivirus signature updates happen daily and are left out.
        foreach (var e in QueryWindow("System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=19", ChangeWindow))
        {
            var title = (Prop(e, 0) ?? "").Trim();
            if (title.Length == 0 || title.Contains("Security Intelligence", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(title, "^[0-9A-Z]{12}-")) continue; // Store app updates are left out as well
            list.Add(new RecentChange(RecentChangeKind.Update, title, e.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow));
        }
        // Drivers: UserPnp 20001 (driver installed) and 20003 (driver service added); readable without admin rights.
        foreach (var e in QueryWindow("System", "Provider[@Name='Microsoft-Windows-UserPnp'] and (EventID=20001 or EventID=20003)", ChangeWindow))
        {
            var name = (Prop(e, 0) ?? "").Trim();
            if (name.Length > 0) list.Add(new RecentChange(RecentChangeKind.Driver, name, e.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow));
        }
        s.RecentChanges = list.GroupBy(c => (c.Kind, c.Name.ToLowerInvariant())).Select(g => g.OrderByDescending(c => c.WhenUtc).First())
            .OrderByDescending(c => c.WhenUtc).Take(40).ToList();
    }

    /// <summary>"yyyymmdd" as written by installers; null for anything else.</summary>
    public static DateTime? ParseInstallDate(string? v) =>
        v is { Length: 8 } && DateTime.TryParseExact(v, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>System Restore points through WMI (root\default). Needs administrator rights on most machines: then the answer stays Unknown.</summary>
    private static void ReadRestorePoints(HealthState s)
    {
        try
        {
            var dates = new List<DateTime>();
            using var q = new System.Management.ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
            foreach (System.Management.ManagementObject o in q.Get())
                using (o)
                    if (o["CreationTime"] is string t) dates.Add(System.Management.ManagementDateTimeConverter.ToDateTime(t).ToUniversalTime());
            s.RestorePoints = dates.OrderByDescending(d => d).ToList();
            s.RestorePointsKnown = Tri.Yes;
        }
        catch (Exception) { s.RestorePointsKnown = Tri.Unknown; }
    }

    private static readonly System.Text.RegularExpressions.Regex LiveKernelCode =
        new(@"\bP1\s*:\s*(141|117)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void ReadGraphicsResets(HealthState s)
    {
        var n = Query("System", "Provider[@Name='Display'] and EventID=4101").Count();
        foreach (var e in Query("Application", "Provider[@Name='Windows Error Reporting'] and EventID=1001"))
        {
            string text;
            try { text = e.FormatDescription() ?? ""; } catch (Exception) { text = ""; }
            if (text.Length == 0) text = string.Join(" ", e.Properties.Select(p => p.Value?.ToString() ?? ""));
            if (text.Contains("LiveKernelEvent", StringComparison.OrdinalIgnoreCase) && LiveKernelCode.IsMatch(text)) n++;
        }
        s.GraphicsResets = n;
    }
    // ---- devices and profile ----

    private static IReadOnlyList<DeviceProblem> ReadDeviceProblems()
    {
        var list = new List<DeviceProblem>();
        using var q = new System.Management.ManagementObjectSearcher(
            "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        foreach (System.Management.ManagementObject d in q.Get())
            using (d)
            {
                var code = Convert.ToInt32(d["ConfigManagerErrorCode"] ?? 0);
                if (DeviceErrors.IsNoise(code) || (d["Present"] is bool p && !p)) continue;
                var id = d["DeviceID"] as string ?? "";
                list.Add(new DeviceProblem((d["Name"] as string ?? "unknown device").Trim(), id, d["PNPClass"] as string ?? "", code));
            }
        return list.OrderBy(x => x.DisabledByPerson).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadProfile(HealthState s)
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parts = new List<string>();
        if (TempProfiles.IsTemporaryPath(path)) parts.Add("profile folder is a TEMP folder");
        var n = 0;
        using (var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName,
            "*[System[Provider[@Name='Microsoft-Windows-User Profiles Service'] and (EventID=1511 or EventID=1515) and TimeCreated[timediff(@SystemTime) <= 172800000]]]")))
            while (n < 50 && reader.ReadEvent() is { } e) using (e) n++;
        if (n > 0) parts.Add($"User Profile Service events 1511/1515 in 2 days: {n}");
        s.TempProfile = parts.Count > 0;
        s.TempProfileDetail = string.Join("; ", parts);
    }

    // ---- drives ----

    /// <summary>Windows Storage health per physical drive, plus Win32_DiskDrive "Pred Fail" (the drive's own failure prediction).</summary>
    private static IReadOnlyList<PhysicalDriveInfo> ReadDrives()
    {
        var predict = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var q = new System.Management.ManagementObjectSearcher("SELECT Model, Status FROM Win32_DiskDrive"))
            foreach (System.Management.ManagementObject d in q.Get())
                using (d)
                    if (string.Equals(d["Status"] as string, "Pred Fail", StringComparison.OrdinalIgnoreCase)) predict.Add((d["Model"] as string ?? "").Trim());

        var list = new List<PhysicalDriveInfo>();
        using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT FriendlyName, MediaType, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk");
        foreach (System.Management.ManagementObject d in s.Get())
            using (d)
            {
                var name = (d["FriendlyName"] as string ?? "drive").Trim();
                list.Add(new PhysicalDriveInfo(name, MediaName(d["MediaType"]), HealthName(d["HealthStatus"]),
                    (d["OperationalStatus"] as ushort[] ?? Array.Empty<ushort>()).Select(x => (int)x).ToList(), predict.Contains(name)));
            }
        return list;
    }

    public static string HealthName(object? v) => Convert.ToInt32(v ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };

    private static string MediaName(object? v) => Convert.ToInt32(v ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" };
    // ---- clock ----

    /// <summary>Compares the clock with the Date header of Microsoft's connectivity test (plain HTTP, so a wrong clock cannot break the check itself).</summary>
    private static double? ReadClockOffset()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var sent = DateTime.UtcNow;
        using var r = http.Send(new HttpRequestMessage(HttpMethod.Head, "http://www.msftconnecttest.com/connecttest.txt"));
        var received = DateTime.UtcNow;
        if (r.Headers.Date is not { } server) return null;
        var local = sent + (received - sent) / 2;
        return (local - server.UtcDateTime).TotalSeconds;
    }

    // ---- proxy ----

    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static void ReadUserProxy(HealthState s)
    {
        using var k = Registry.CurrentUser.OpenSubKey(InternetSettings);
        s.UserProxyEnabled = k?.GetValue("ProxyEnable") is int e && e != 0;
        s.UserProxyServer = k?.GetValue("ProxyServer") as string;
        s.AutoConfigUrl = k?.GetValue("AutoConfigURL") as string;
        if (s.UserProxyEnabled && !string.IsNullOrWhiteSpace(s.UserProxyServer))
            s.UserProxyReachable = Reachable(s.UserProxyServer);
        else if (!string.IsNullOrWhiteSpace(s.AutoConfigUrl))
            s.UserProxyReachable = ScriptReachable(s.AutoConfigUrl);
    }

    /// <summary>WinHttpSettings: DWORD size, DWORD counter, DWORD flags (1 = direct), DWORD length, proxy string (ANSI).</summary>
    private static void ReadWinHttpProxy(HealthState s)
    {
        using var k = Registry.LocalMachine.OpenSubKey(InternetSettings + @"\Connections");
        if (k?.GetValue("WinHttpSettings") is not byte[] b || b.Length < 16) return;
        var flags = BitConverter.ToInt32(b, 8);
        var len = BitConverter.ToInt32(b, 12);
        if ((flags & 2) == 0 || len <= 0 || 16 + len > b.Length) return;
        s.WinHttpProxy = Encoding.ASCII.GetString(b, 16, len);
        s.WinHttpProxyReachable = Reachable(s.WinHttpProxy);
    }

    /// <summary>"host:port", "http=host:port;https=host:port" or "host" (port 80). Reachable if any listed server accepts a connection.</summary>
    public static IReadOnlyList<(string Host, int Port)> ParseProxyList(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Contains('=') ? p[(p.IndexOf('=') + 1)..] : p)
            .Select(p => p.Replace("http://", "", StringComparison.OrdinalIgnoreCase).Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/'))
            .Where(p => p.Length > 0)
            .Select(p =>
            {
                var colon = p.LastIndexOf(':');
                return colon > 0 && int.TryParse(p[(colon + 1)..], out var port) ? (p[..colon], port) : (p, 80);
            })
            .Distinct().ToList();

    private static Tri Reachable(string value)
    {
        var servers = ParseProxyList(value);
        if (servers.Count == 0) return Tri.Unknown;
        foreach (var (host, port) in servers)
        {
            try
            {
                using var c = new TcpClient();
                if (c.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(3)) && c.Connected) return Tri.Yes;
            }
            catch (Exception) { }
        }
        return Tri.No;
    }

    private static Tri ScriptReachable(string url)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            using var r = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            return r.IsSuccessStatusCode ? Tri.Yes : Tri.No;
        }
        catch (Exception) { return Tri.No; }
    }

    // ---- Winsock, PATH, services, Store apps, restart ----

    private static readonly string[] WindowsWinsock = { "mswsock.dll", "rsvpsp.dll", "wshbth.dll", "wshqos.dll", "vsocklib.dll", "hvsocket.dll", "wshhyperv.dll", "napinsp.dll", "pnrpnsp.dll", "nlansp_c.dll", "winrnr.dll", "wshtcpip.dll", "wshirda.dll" };

    private static IReadOnlyList<string> ReadWinsock()
    {
        var found = new List<string>();
        foreach (var catalog in new[] { "Catalog_Entries64", "Catalog_Entries" })
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\{catalog}");
            if (k is null) continue;
            foreach (var name in k.GetSubKeyNames())
            {
                using var e = k.OpenSubKey(name);
                if (e?.GetValue("PackedCatalogItem") is not byte[] b) continue;
                var dll = Encoding.ASCII.GetString(b, 0, Math.Min(260, b.Length)).Split('\0')[0];
                if (dll.Length > 0 && !WindowsWinsock.Contains(Path.GetFileName(dll), StringComparer.OrdinalIgnoreCase)) found.Add(dll);
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? ReadMachinePath()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        return k?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static UpdateServiceInfo ReadService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return new UpdateServiceInfo(name, sc.Status == ServiceControllerStatus.Running ? "Running" : sc.Status == ServiceControllerStatus.Stopped ? "Stopped" : sc.Status.ToString(), sc.StartType.ToString());
        }
        catch (InvalidOperationException) { return new UpdateServiceInfo(name, "NotFound", "NotFound"); }
        catch (Exception) { return new UpdateServiceInfo(name, "Unknown", "Unknown"); }
    }

    /// <summary>Get-AppxPackage for the current user; only packages whose status is not Ok.</summary>
    private static IReadOnlyList<StoreAppInfo> ReadBrokenStoreApps()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; @(Get-AppxPackage | Where-Object { $_.Status -ne 'Ok' } | Select-Object PackageFullName,Name,@{n='Status';e={[string]$_.Status}}) | ConvertTo-Json -Compress" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var json = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } throw new System.TimeoutException("Get-AppxPackage did not finish"); }
        return ParseStoreApps(json);
    }

    public static IReadOnlyList<StoreAppInfo> ParseStoreApps(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return Array.Empty<StoreAppInfo>();
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        return items.Select(i => new StoreAppInfo(i.GetProperty("PackageFullName").GetString() ?? "", i.GetProperty("Name").GetString() ?? "",
            i.GetProperty("Status").ValueKind == JsonValueKind.String ? i.GetProperty("Status").GetString() ?? "" : i.GetProperty("Status").ToString()))
            .Where(a => a.FullName.Length > 0).ToList();
    }

    private static Tri ReadRebootPending()
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            if (k != null) return Tri.Yes;
        }
        return Tri.No;
    }
}
