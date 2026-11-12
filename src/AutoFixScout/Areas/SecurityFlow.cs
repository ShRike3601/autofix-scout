using System.Management;
using System.ServiceProcess;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Microsoft.Win32;

namespace AutoFixScout.Areas;

/// <summary>"Security and unwanted software". Real test: the person's answer; the re-check is SecurityTriage.Resolved.</summary>
public static class SecurityFlow
{
    public const string Topic = "security";
    public const string What = "Security and unwanted software";
    public const string TestQuestion = "Does \"Windows Security\" now show that your computer is protected?";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        var s = Read();
        var f = SecurityTriage.Evaluate(s);
        return new Diagnosis(s, f, SecurityTriage.Ladder(s, f), false, SecurityTriage.NotFixedHint(f));
    });

    private const string FirewallPolicy = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
    private static readonly (string Key, string Name)[] Profiles = { ("DomainProfile", "Domain"), ("StandardProfile", "Private"), ("PublicProfile", "Public") };

    /// <summary>Read-only, no admin. Each part is read separately; a part that fails is named in ProbeErrors and stays unknown.</summary>
    public static SecurityState Read()
    {
        var s = new SecurityState { CheckedUtc = DateTime.UtcNow };
        var errors = new List<string>();
        void Part(string name, Action read)
        {
            try { read(); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }

        Part("antivirus", () => s.Antivirus = ReadProducts("AntiVirusProduct"));
        Part("firewall products", () => s.Firewalls = ReadProducts("FirewallProduct"));
        if (SecurityTriage.DefenderInCharge(s))
            ReadDefender(s); // denied or missing only leaves these details unknown
        Part("firewall", () =>
        {
            var profiles = new Dictionary<string, Tri>();
            using var k = Registry.LocalMachine.OpenSubKey(FirewallPolicy);
            foreach (var (key, name) in Profiles)
            {
                using var p = k?.OpenSubKey(key);
                profiles[name] = p?.GetValue("EnableFirewall") is int v ? (v != 0 ? Tri.Yes : Tri.No) : Tri.Unknown;
            }
            s.FirewallProfiles = profiles;
        });
        Part("firewall service", () =>
        {
            try
            {
                using var sc = new ServiceController("mpssvc");
                s.FirewallService = new UpdateServiceInfo("mpssvc", sc.Status == ServiceControllerStatus.Running ? "Running" : sc.Status == ServiceControllerStatus.Stopped ? "Stopped" : sc.Status.ToString(), sc.StartType.ToString());
            }
            catch (InvalidOperationException) { s.FirewallService = new UpdateServiceInfo("mpssvc", "NotFound", "NotFound"); }
        });
        Part("user account control", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            // EnableLUA missing means Windows' default: on.
            s.UacEnabled = k?.GetValue("EnableLUA") is int lua ? (lua != 0 ? Tri.Yes : Tri.No) : Tri.Yes;
            s.UacAdminPrompt = k?.GetValue("ConsentPromptBehaviorAdmin") as int?;
        });
        Part("SmartScreen", () =>
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer"))
                s.SmartScreenSetting = k?.GetValue("SmartScreenEnabled") as string;
            using (var p = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System"))
                s.SmartScreenPolicy = p?.GetValue("EnableSmartScreen") as int?;
        });
        Part("hosts file and proxy", () =>
        {
            s.Network = RealHealthProbe.ReadHostsAndProxy();
            errors.AddRange(s.Network.ProbeErrors);
        });
        Part("startup programs", () => s.SuspiciousStartups = ReadSuspiciousStartups());
        Part("browser policies", () =>
        {
            var flags = new List<BrowserPolicies.Flag>();
            flags.AddRange(BrowserPolicies.Scan(Registry.CurrentUser, BrowserPolicies.HiveUser));
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                flags.AddRange(BrowserPolicies.Scan(hklm, BrowserPolicies.HiveMachine));
            s.BrowserPolicyFlags = flags;
            s.ManagedPc = flags.Count > 0 && BrowserPolicies.IsManagedPc();
        });
        s.ProbeErrors = errors;
        return s;
    }

    private static IReadOnlyList<SecurityProduct> ReadProducts(string cls)
    {
        using var q = new ManagementObjectSearcher(@"root\SecurityCenter2", $"SELECT * FROM {cls}");
        return q.Get().Cast<ManagementBaseObject>()
            .Select(m => new SecurityProduct(m["displayName"] as string ?? "", Convert.ToInt32(m["productState"] ?? 0), m["pathToSignedProductExe"] as string))
            .Where(p => p.Name.Length > 0).ToList();
    }

    private static void ReadDefender(SecurityState s)
    {
        try
        {
            using var q = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT RealTimeProtectionEnabled, AntivirusSignatureLastUpdated, QuickScanAge FROM MSFT_MpComputerStatus");
            foreach (var m in q.Get().Cast<ManagementBaseObject>())
            {
                using (m)
                {
                    if (m["RealTimeProtectionEnabled"] is bool rtp) s.DefenderRealTime = rtp ? Tri.Yes : Tri.No;
                    if (m["AntivirusSignatureLastUpdated"] is string when && when.Length > 0)
                        s.DefenderSignaturesUtc = ManagementDateTimeConverter.ToDateTime(when).ToUniversalTime();
                    if (m["QuickScanAge"] is { } age && Convert.ToInt64(age) is var days and >= 0 and < 100_000) s.DefenderQuickScanAgeDays = (int)days;
                }
                return;
            }
            s.DefenderNote = "MSFT_MpComputerStatus: no instance";
        }
        catch (Exception ex) { s.DefenderNote = "MSFT_MpComputerStatus: " + ex.Message; }
    }

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static IReadOnlyList<SuspiciousStartup> ReadSuspiciousStartups()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pub = Environment.GetEnvironmentVariable("PUBLIC") ?? "";
        var found = new List<SuspiciousStartup>();
        void Scan(RegistryKey hive, string location, string path)
        {
            using var k = hive.OpenSubKey(path);
            if (k is null) return;
            foreach (var name in k.GetValueNames())
                if (k.GetValue(name) is string cmd && SecurityTriage.IsSuspiciousStartup(cmd, temp, appData, pub))
                    found.Add(new SuspiciousStartup(location, name, SecurityTriage.ExecutableOf(cmd) ?? cmd));
        }
        using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            Scan(hkcu, @"HKCU\Run", RunKey);
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            Scan(hklm, @"HKLM\Run", RunKey);
            Scan(hklm, @"HKLM\Run (32-bit)", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");
        }
        return found;
    }
}
