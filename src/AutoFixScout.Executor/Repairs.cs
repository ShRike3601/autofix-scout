using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using AutoFixScout.Repair;
using Microsoft.Win32;

namespace AutoFixScout.Executor;

/// <summary>
/// System repairs for the health check. Each takes no free-form input: either the fixed word "system", a name from a
/// built-in list, or a value the executor checks itself. Anything changed is backed up first in
/// C:\ProgramData\AutoFixScout\backups, and the tool output is kept in C:\ProgramData\AutoFixScout\logs.
/// Exit codes as in Program: 0 done, 1 failed, 3 nothing to do.
/// </summary>
public static class Repairs
{
    public const string System = "system";
    public static readonly string[] RuntimeArchitectures = { "x64", "x86", "arm64" };

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFixScout");
    private static string Stamp => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    private static string Backup(string name, string content)
    {
        var dir = Path.Combine(Root, "backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}-{Stamp}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static void Log(string action, string text)
    {
        try
        {
            var dir = Path.Combine(Root, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"{action}-{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:HH:mm:ss}] {text}\n");
        }
        catch (Exception) { }
    }

    /// <summary>Runs a Windows tool from System32 with fixed arguments. Returns exit code and output.</summary>
    internal static (int Code, string Output) Tool(string exe, Encoding? encoding, int timeoutMinutes, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (encoding != null) psi.StandardOutputEncoding = encoding;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes))) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        var output = (stdout.Result + stderr.Result).Replace("\0", "");
        Log(Path.GetFileNameWithoutExtension(exe), $"{exe} {string.Join(' ', args)} -> {p.ExitCode}\n{output}");
        return (p.ExitCode, output);
    }


    /// <summary>
    /// Best effort: a Windows restore point before a system-wide change, so a helper can roll the whole computer back.
    /// Windows creates at most one per 24 hours by default and may have System Protection off; neither stops the repair.
    /// </summary>
    internal static void RestorePoint(string action)
    {
        try
        {
            using var sr = new System.Management.ManagementClass(@"\.\root\default", "SystemRestore", null);
            // 12 = MODIFY_SETTINGS, 100 = BEGIN_SYSTEM_CHANGE.
            var result = sr.InvokeMethod("CreateRestorePoint", new object[] { $"AutoFix Scout: {action}", 12, 100 });
            Log(action, $"restore point result {result}");
        }
        catch (Exception ex) { Log(action, $"restore point not created: {ex.Message}"); }
    }
    // ---- system files ----

    public static int Dism() => Tool("dism.exe", null, 90, "/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart").Code == 0 ? 0 : 1;

    /// <summary>sfc writes UTF-16. Exit code 0 also covers "found and repaired"; the log says which.</summary>
    public static int Sfc()
    {
        var (code, _) = Tool("sfc.exe", Encoding.Unicode, 90, "/scannow");
        return code == 0 ? 0 : 1;
    }

    public static int DiskScan(string letter)
    {
        // chkdsk /scan: online scan, the drive stays usable. 0 = no errors, 1 = errors found and fixed.
        var (code, _) = Tool("chkdsk.exe", null, 120, letter + ":", "/scan");
        return code is 0 or 1 ? 0 : 1;
    }

    /// <summary>
    /// Removes superseded versions of Windows components from the component store (WinSxS), as the "Windows Update
    /// Cleanup" in Disk Cleanup does. No /ResetBase. 3010 = done, restart suggested.
    /// </summary>
    public static int CleanupComponents() =>
        Tool("dism.exe", null, 90, "/Online", "/Cleanup-Image", "/StartComponentCleanup").Code is 0 or 3010 ? 0 : 1;

    public static bool IsSystemDrive(string letter) =>
        letter.Length == 1 && char.ToUpperInvariant(letter[0]) == char.ToUpperInvariant(Environment.SystemDirectory[0]);

    // ---- clock ----

    public static int TimeResync()
    {
        SetStart("W32Time", "demand", onlyIfDisabled: true);
        StartService("W32Time");
        if (Tool("w32tm.exe", null, 2, "/resync", "/force").Code == 0) return 0;
        // A computer in a company domain takes its time from the domain; never change that.
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
        var type = k?.GetValue("Type") as string;
        if (string.Equals(type, "NT5DS", StringComparison.OrdinalIgnoreCase)) return 1;
        Backup("w32time", $"Type={type}\nNtpServer={k?.GetValue("NtpServer")}\n");
        Tool("w32tm.exe", null, 2, "/config", "/manualpeerlist:time.windows.com,0x9", "/syncfromflags:manual", "/update");
        Thread.Sleep(2000);
        return Tool("w32tm.exe", null, 2, "/resync", "/force").Code == 0 ? 0 : 1;
    }

    // ---- network ----

    public static int ResetWinHttpProxy()
    {
        RestorePoint("network.reset_winhttp_proxy");
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections"))
            Backup("winhttp-proxy", "WinHttpSettings (base64)=" + Convert.ToBase64String(k?.GetValue("WinHttpSettings") as byte[] ?? Array.Empty<byte>()) + "\n"
                                    + Tool("netsh.exe", null, 1, "winhttp", "show", "proxy").Output);
        return Tool("netsh.exe", null, 1, "winhttp", "reset", "proxy").Code == 0 ? 0 : 1;
    }

    public static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    public static int CleanHosts()
    {
        var lines = File.ReadAllLines(HostsPath);
        if (HostsFile.Suspicious(lines).Count == 0) return 3;
        RestorePoint("network.clean_hosts");
        Backup("hosts", string.Join("\r\n", lines));
        File.WriteAllLines(HostsPath, HostsFile.Clean(lines, DateTime.UtcNow), new UTF8Encoding(false));
        Tool("ipconfig.exe", null, 1, "/flushdns");
        return HostsFile.Suspicious(File.ReadAllLines(HostsPath)).Count == 0 ? 0 : 1;
    }

    public static int ResetWinsock()
    {
        RestorePoint("network.reset_winsock");
        Backup("winsock-catalog", Tool("netsh.exe", null, 1, "winsock", "show", "catalog").Output);
        return Tool("netsh.exe", null, 2, "winsock", "reset").Code == 0 ? 0 : 1;
    }

    // ---- security ----
    /// <summary>
    /// security.remove_browser_policies: removes the flagged browser policy values under HKLM and HKCU (the list is computed here,
    /// from <see cref="BrowserPolicies"/>; nothing is taken as input). The values are saved first. A computer that belongs
    /// to a domain or Azure AD is managed by an organization, so nothing is removed there (3). 3 = nothing flagged.
    /// </summary>
    public static int RemoveBrowserPolicies()
    {
        if (BrowserPolicies.IsManagedPc()) return 3;
        // HKCU\Software\Policies is read-only for the person's normal (non-elevated) process, so both hives are cleaned here.
        // The executor runs elevated as the same user, so CurrentUser is the person's own hive.
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var machine = BrowserPolicies.Scan(hklm, BrowserPolicies.HiveMachine);
        var user = BrowserPolicies.Scan(hkcu, BrowserPolicies.HiveUser);
        if (machine.Count + user.Count == 0) return 3;
        Backup("browser-policies", BrowserPolicies.BackupText(hklm, machine) + BrowserPolicies.BackupText(hkcu, user));
        BrowserPolicies.Remove(hklm, machine);
        BrowserPolicies.Remove(hkcu, user);
        return BrowserPolicies.Scan(hklm, BrowserPolicies.HiveMachine).Count + BrowserPolicies.Scan(hkcu, BrowserPolicies.HiveUser).Count == 0 ? 0 : 1;
    }


    private const string FirewallPolicy = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    /// <summary>
    /// security.firewall_on: Windows Defender Firewall on for every profile (netsh, fixed arguments). The current
    /// settings (netsh output and the EnableFirewall values) are saved first. 3 = already on everywhere.
    /// </summary>
    public static int FirewallOn()
    {
        var values = new StringBuilder();
        var allOn = true;
        using (var k = Registry.LocalMachine.OpenSubKey(FirewallPolicy))
            foreach (var profile in new[] { "DomainProfile", "StandardProfile", "PublicProfile" })
            {
                using var p = k?.OpenSubKey(profile);
                var v = p?.GetValue("EnableFirewall");
                values.Append($"{profile} EnableFirewall={v}\n");
                if (v is not int i || i == 0) allOn = false;
            }
        if (allOn) return 3;
        Backup("firewall", values + "\n" + Tool("netsh.exe", null, 1, "advfirewall", "show", "allprofiles").Output);
        return Tool("netsh.exe", null, 1, "advfirewall", "set", "allprofiles", "state", "on").Code == 0 ? 0 : 1;
    }

    private const string UacPolicy = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>
    /// security.uac_on: User Account Control back on (EnableLUA=1) and, if it was set to never ask, the Windows default
    /// prompt for administrators (ConsentPromptBehaviorAdmin=5). The old values are saved first. Takes effect after a restart.
    /// 3 = already on.
    /// </summary>
    public static int UacOn()
    {
        using var k = Registry.LocalMachine.OpenSubKey(UacPolicy, writable: true);
        if (k is null) return 1;
        var lua = k.GetValue("EnableLUA");
        var prompt = k.GetValue("ConsentPromptBehaviorAdmin");
        var luaOff = lua is int l && l == 0;
        var neverAsk = prompt is int c && c == 0;
        if (!luaOff && !neverAsk) return 3;
        Backup("uac", $"EnableLUA={lua}\nConsentPromptBehaviorAdmin={prompt}\n");
        if (luaOff) k.SetValue("EnableLUA", 1, RegistryValueKind.DWord);
        if (neverAsk) k.SetValue("ConsentPromptBehaviorAdmin", 5, RegistryValueKind.DWord);
        Log("security.uac_on", $"EnableLUA {lua} -> {k.GetValue("EnableLUA")}, ConsentPromptBehaviorAdmin {prompt} -> {k.GetValue("ConsentPromptBehaviorAdmin")}");
        return k.GetValue("EnableLUA") is 1 && k.GetValue("ConsentPromptBehaviorAdmin") is not 0 ? 0 : 1;
    }

    // ---- services ----

    private static void SetStart(string service, string start, bool onlyIfDisabled = false)
    {
        if (onlyIfDisabled)
        {
            using var sc = new ServiceController(service);
            if (sc.StartType != ServiceStartMode.Disabled) return;
        }
        Tool("sc.exe", null, 1, "config", service, "start=", start);
    }

    private static void StartService(string service)
    {
        using var sc = new ServiceController(service);
        if (sc.Status == ServiceControllerStatus.Running) return;
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    private static void StopService(string service)
    {
        using var sc = new ServiceController(service);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    /// <summary>Only services in <see cref="ServiceDefaults.All"/>, only to the start type listed there.</summary>
    public static int RestoreServiceDefault(string name)
    {
        if (ServiceDefaults.Find(name) is not { } d) return 2;
        using (var sc = new ServiceController(d.Name))
            Backup($"service-{d.Name}", $"{d.Name} start={sc.StartType} status={sc.Status}\n");
        var (code, _) = Tool("sc.exe", null, 1, "config", d.Name, "start=", d.Start == ServiceDefaults.Start.Manual ? "demand" : d.Delayed ? "delayed-auto" : "auto");
        if (code != 0) return 1;
        if (d.ShouldRun) StartService(d.Name);
        return 0;
    }

    // ---- PATH ----

    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    public static int RepairPath()
    {
        using var k = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true);
        if (k is null) return 1;
        var raw = k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (SystemPath.Missing(raw, root).Count == 0) return 3;
        RestorePoint("system.repair_path");
        Backup("system-path", raw ?? "");
        k.SetValue("Path", SystemPath.Repair(raw, root), RegistryValueKind.ExpandString);
        SendMessageTimeout((IntPtr)0xffff, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
        return 0;
    }

    /// <summary>
    /// system.pagefile_auto: lets Windows manage the page file size (Win32_ComputerSystem.AutomaticManagedPagefile = true).
    /// The current settings are saved first. Takes effect after a restart. 3 = already automatic.
    /// </summary>
    public static int PageFileAuto()
    {
        var scope = new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { EnablePrivileges = true });
        scope.Connect();
        using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_ComputerSystem"));
        foreach (ManagementObject cs in q.Get())
            using (cs)
            {
                if (cs["AutomaticManagedPagefile"] is true) return 3;
                var text = new StringBuilder();
                text.Append($"AutomaticManagedPagefile={cs["AutomaticManagedPagefile"]}\n");
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting")))
                    foreach (ManagementObject o in s.Get())
                        using (o) text.Append($"Setting {o["Name"]} InitialSize={o["InitialSize"]} MaximumSize={o["MaximumSize"]}\n");
                using (var u = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage")))
                    foreach (ManagementObject o in u.Get())
                        using (o) text.Append($"Usage {o["Name"]} AllocatedBaseSize={o["AllocatedBaseSize"]} CurrentUsage={o["CurrentUsage"]}\n");
                RestorePoint("system.pagefile_auto");
                Backup("pagefile", text.ToString());
                cs["AutomaticManagedPagefile"] = true;
                cs.Put();
                return 0;
            }
        return 1;
    }


    // ---- Wi-Fi stability on enterprise networks ----

    private const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// network.wifi_stability: applies <see cref="WifiTuning"/> to one Wi-Fi adapter (validated instance id), only for
    /// values its driver supports. Saves the old values, restarts the adapter, and puts everything back (exit 4) if the
    /// connection is not back within 4 minutes. 3 = already tuned or not supported. Same steps that fixed the test bench.
    /// </summary>
    public static int WifiStability(string instanceId)
    {
        using var a = new ManagementObjectSearcher($"SELECT GUID FROM Win32_NetworkAdapter WHERE PNPDeviceID = '{instanceId.Replace(@"\", @"\\")}'");
        var guid = a.Get().Cast<ManagementBaseObject>().Select(x => x["GUID"] as string).FirstOrDefault(g => g != null);
        if (guid is null) return 1;
        using var cls = Registry.LocalMachine.OpenSubKey(NetClass);
        var keyName = cls?.GetSubKeyNames().FirstOrDefault(n =>
        {
            using var k = cls.OpenSubKey(n);
            return string.Equals(k?.GetValue("NetCfgInstanceId") as string, guid, StringComparison.OrdinalIgnoreCase);
        });
        if (keyName is null) return 1;
        using var key = cls!.OpenSubKey(keyName, writable: true)!;
        var current = new Dictionary<string, string>();
        var supported = new Dictionary<string, IReadOnlyCollection<string>>();
        foreach (var (kw, _, _) in WifiTuning.Targets)
        {
            if (key.GetValue(kw) is { } v) current[kw] = v.ToString()!;
            using var e = key.OpenSubKey($@"Ndi\Params\{kw}\Enum");
            if (e != null) supported[kw] = e.GetValueNames();
        }
        var plan = WifiTuning.Plan(current, supported);
        if (plan.Count == 0) return 3;
        Backup("wifi-stability", string.Join("\n", plan.Select(p => $"{p.Keyword}={p.From} (set to {p.To})")) + "\n");
        foreach (var p in plan) key.SetValue(p.Keyword, p.To, RegistryValueKind.String);
        RestartAdapter(instanceId);
        if (WaitForInternet(guid, TimeSpan.FromMinutes(4))) return 0;
        Log("network.wifi_stability", "no connection after 4 minutes: restoring the previous values");
        foreach (var p in plan) key.SetValue(p.Keyword, p.From, RegistryValueKind.String);
        RestartAdapter(instanceId);
        return 4;
    }

    private static void RestartAdapter(string instanceId)
    {
        // pnputil /restart-device exists on Windows 10 2004+; otherwise disable and enable.
        if (Program.Pnputil("/restart-device", instanceId) != 0)
        {
            Program.Pnputil("/disable-device", instanceId);
            Program.Pnputil("/enable-device", instanceId);
        }
    }

    private static bool WaitForInternet(string adapterGuid, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(3000);
            var up = global::System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => string.Equals(n.Id, adapterGuid, StringComparison.OrdinalIgnoreCase) && n.OperationalStatus == global::System.Net.NetworkInformation.OperationalStatus.Up);
            if (!up) continue;
            try
            {
                using var c = new global::System.Net.Sockets.TcpClient();
                if (c.ConnectAsync("www.msftconnecttest.com", 80).Wait(TimeSpan.FromSeconds(5)) && c.Connected) return true;
            }
            catch (Exception) { }
        }
        return false;
    }
    // ---- Windows Update, search ----

    private static readonly string[] UpdateServices = { "wuauserv", "BITS", "CryptSvc" };

    /// <summary>The classic Windows Update reset: stop the services, rename (not delete) the download and catalog folders, start again.</summary>
    public static int ResetUpdateComponents()
    {
        RestorePoint("update.reset_components");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var folders = new[] { Path.Combine(windows, "SoftwareDistribution"), Path.Combine(Environment.SystemDirectory, "catroot2") };
        var stamp = Stamp;
        var renamed = 0;
        try
        {
            foreach (var s in UpdateServices) StopService(s);
            foreach (var f in folders.Where(Directory.Exists))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { Directory.Move(f, $"{f}.afs-{stamp}"); renamed++; break; }
                    catch (IOException) when (attempt < 5)
                    {
                        // wuauserv can be started again by a trigger; stop it and retry.
                        foreach (var s in UpdateServices) StopService(s);
                        Thread.Sleep(1000);
                    }
                }
            }
        }
        finally
        {
            foreach (var s in UpdateServices) { try { StartService(s); } catch (Exception ex) { Log("update.reset_components", $"start {s}: {ex.Message}"); } }
        }
        Log("update.reset_components", $"renamed {renamed} folders with suffix .afs-{stamp}");
        return renamed > 0 ? 0 : 1;
    }

    public static int RebuildSearchIndex()
    {
        StopService("WSearch");
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Search", writable: true))
            k?.SetValue("SetupCompletedSuccessfully", 0, RegistryValueKind.DWord);
        SetStart("WSearch", "delayed-auto", onlyIfDisabled: true);
        StartService("WSearch");
        return 0;
    }

    // ---- Microsoft Office ----

    /// <summary>
    /// Office Quick Repair (Click-to-Run only). Platform and language are read here from the machine registry and checked;
    /// the arguments are fixed text. The program must be Office's own, Microsoft-signed. 3 = Click-to-Run Office not installed.
    /// </summary>
    public static int OfficeQuickRepair()
    {
        if (OfficeClickToRun.Read() is not { } config) return 3;
        if (OfficeClickToRun.QuickRepairArgs(config.Platform, config.Culture) is not { } args)
        {
            Log("office.quick_repair", "platform or language in the registry not recognised");
            return 1;
        }
        var exe = OfficeClickToRun.ExePath;
        if (!File.Exists(exe) || !IsMicrosoftSigned(exe)) { Log("office.quick_repair", "OfficeClickToRun.exe missing or not signed by Microsoft"); return 1; }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(TimeSpan.FromMinutes(60))) { Log("office.quick_repair", "timed out"); return 1; }
        Log("office.quick_repair", $"OfficeClickToRun.exe {string.Join(' ', args)} -> {p.ExitCode}");
        return p.ExitCode == 0 ? 0 : 1;
    }

    // ---- Visual C++ runtime ----

    /// <summary>
    /// Downloads Microsoft's permanent link for the latest supported Visual C++ runtime, refuses to run it unless it carries
    /// a valid Microsoft Authenticode signature, then installs (or repairs, when the same version is already there).
    /// </summary>
    public static int InstallVcRedist(string arch)
    {
        if (!RuntimeArchitectures.Contains(arch)) return 2;
        var dir = Path.Combine(Root, "downloads");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"vc_redist.{arch}.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            var bytes = http.GetByteArrayAsync($"https://aka.ms/vs/17/release/vc_redist.{arch}.exe").GetAwaiter().GetResult();
            File.WriteAllBytes(file, bytes);
        }
        try
        {
            if (!IsMicrosoftSigned(file)) { Log("runtime.install_vcredist", "signature check failed"); return 1; }
            var code = RunInstaller(file, "/install");
            if (code == 1638) code = RunInstaller(file, "/repair"); // this version is already installed: repair it
            return code is 0 or 3010 ? 0 : 1;
        }
        finally { try { File.Delete(file); } catch (Exception) { } }
    }

    private static int RunInstaller(string file, string mode)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { mode, "/quiet", "/norestart" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(TimeSpan.FromMinutes(20));
        Log("runtime.install_vcredist", $"{Path.GetFileName(file)} {mode} -> {p.ExitCode}");
        return p.ExitCode;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public uint Size; public string Path; public IntPtr File; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size; public IntPtr Policy; public IntPtr SipData; public uint UiChoice; public uint RevocationChecks;
        public uint UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference;
        public uint ProvFlags; public uint UiContext; public IntPtr SignatureSettings;
    }

    /// <summary>Valid Authenticode chain (WinVerifyTrust) and the signer is Microsoft Corporation.</summary>
    public static bool IsMicrosoftSigned(string file)
    {
        var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), Path = file };
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2 /* none */, RevocationChecks = 0, UnionChoice = 1 /* file */,
                FileInfo = infoPtr, StateAction = 0, ProvFlags = 0x10 /* safer */,
            };
            var generic = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            if (WinVerifyTrust(new IntPtr(-1), generic, ref data) != 0) return false;
        }
        finally { Marshal.FreeHGlobal(infoPtr); }
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
        return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
    }
}
