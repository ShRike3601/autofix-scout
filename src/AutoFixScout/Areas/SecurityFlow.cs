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
        using var _ = Activity.Scope(Topic);
        var s = Read();
        var f = Analysis.Judge(() => SecurityTriage.Evaluate(s));
        return new Diagnosis(s, f, Analysis.Plan(SecurityTriage.Ladder(s, f)), false, SecurityTriage.NotFixedHint(f));
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

        Activity.Report(ActivityKind.Analyze, "Reading the security products, firewall, User Account Control and unwanted-software signs");
        Part("antivirus", () => s.Antivirus = ReadProducts("AntiVirusProduct"));
        Part("firewall products", () => s.Firewalls = ReadProducts("FirewallProduct"));
        if (SecurityTriage.DefenderInCharge(s))
            ReadDefender(s); // denied or missing only leaves these details unknown
        if (SecurityTriage.DefenderInCharge(s))
            ReadThreats(s); // a failed read leaves the history Unknown and never blocks other findings
        Part("firewall", () =>
        {
            var profiles = new Dictionary<string, Tri>();
            Activity.ReadingRegistry("HKLM", FirewallPolicy, "(firewall on or off per profile)");
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
            Activity.ReadingRegistry("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "(User Account Control)");
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            // EnableLUA missing means Windows' default: on.
            s.UacEnabled = k?.GetValue("EnableLUA") is int lua ? (lua != 0 ? Tri.Yes : Tri.No) : Tri.Yes;
            s.UacAdminPrompt = k?.GetValue("ConsentPromptBehaviorAdmin") as int?;
        });
        Part("SmartScreen", () =>
        {
            Activity.ReadingRegistry("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "(SmartScreen setting and policy)");
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
            Activity.ReadingRegistry("HKCU and HKLM", @"SOFTWARE\Policies", "(browser policies for Edge, Chrome and Firefox)");
            var flags = new List<BrowserPolicies.Flag>();
            flags.AddRange(BrowserPolicies.Scan(Registry.CurrentUser, BrowserPolicies.HiveUser));
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                flags.AddRange(BrowserPolicies.Scan(hklm, BrowserPolicies.HiveMachine));
            s.BrowserPolicyFlags = flags;
            s.ManagedPc = flags.Count > 0 && BrowserPolicies.IsManagedPc();
        });
        Part("browser search, start pages and extensions", () =>
        {
            Activity.ReadingRegistry("HKCU and HKLM", @"Software\Policies", "(search engine, start pages and forced extensions of Chrome and Edge)");
            Activity.ReadingRegistry("HKCU and HKLM", @"Software\Google\Chrome\Extensions", "(extensions added through the registry for Chrome and Edge)");
            var b = BrowserHijackScan.ScanRegistry();
            s.BrowserHijack = b;
            Activity.Report(ActivityKind.Analyze,
                $"Found {b.SearchHosts.Count + b.StartHosts.Count} unusual browser address settings and {b.Forced.Count + b.External.Count} extensions added by programs");
        });
        s.ProbeErrors = errors;
        return s;
    }

    private static IReadOnlyList<SecurityProduct> ReadProducts(string cls)
    {
        using var q = Wmi.Searcher(@"root\SecurityCenter2", $"SELECT * FROM {cls}");
        return q.Get().Cast<ManagementBaseObject>()
            .Select(m => new SecurityProduct(m["displayName"] as string ?? "", Convert.ToInt32(m["productState"] ?? 0), m["pathToSignedProductExe"] as string))
            .Where(p => p.Name.Length > 0).ToList();
    }

    private static void ReadDefender(SecurityState s)
    {
        try
        {
            using var q = Wmi.Searcher(@"root\Microsoft\Windows\Defender",
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

    private const string DefenderNs = @"root\Microsoft\Windows\Defender";
    private const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";

    /// <summary>Threat history of the last 30 days: WMI first, the operational event log when WMI is denied. Names and counts only, no paths.</summary>
    public static void ReadThreats(SecurityState s)
    {
        var now = DateTime.UtcNow;
        try
        {
            var det = new List<DetectionRow>();
            using (var q = Wmi.Searcher(DefenderNs, "SELECT ThreatID, InitialDetectionTime, ActionSuccess, CurrentThreatExecutionStatusID, Resources FROM MSFT_MpThreatDetection"))
                foreach (var m in q.Get().Cast<ManagementBaseObject>())
                    using (m)
                    {
                        var when = m["InitialDetectionTime"] as string;
                        if (m["ThreatID"] is null || string.IsNullOrEmpty(when)) continue;
                        det.Add(new DetectionRow(Convert.ToString(m["ThreatID"])!, ManagementDateTimeConverter.ToDateTime(when).ToUniversalTime(),
                            m["ActionSuccess"] as bool?, m["CurrentThreatExecutionStatusID"] is { } es ? Convert.ToInt32(es) : null,
                            m["Resources"] is Array res ? res.Length : 0));
                    }
            var thr = new List<ThreatRow>();
            using (var q = Wmi.Searcher(DefenderNs, "SELECT ThreatID, ThreatName, SeverityID, IsActive FROM MSFT_MpThreat"))
                foreach (var m in q.Get().Cast<ManagementBaseObject>())
                    using (m)
                        if (m["ThreatID"] is not null)
                            thr.Add(new ThreatRow(Convert.ToString(m["ThreatID"])!, m["ThreatName"] as string ?? "", m["SeverityID"] is { } sv ? Convert.ToInt32(sv) : null, m["IsActive"] as bool?));
            s.Threats = ThreatHistory.FromWmi(det, thr, now);
            s.ThreatSource = "wmi";
            return;
        }
        catch (Exception ex) { s.ThreatNote = "MSFT_MpThreatDetection: " + ex.Message; }
        try
        {
            var rows = new List<DefenderEventRow>();
            using var reader = EventLogs.Open(DefenderLog,
                $"*[System[(EventID=1116 or EventID=1117 or EventID=1118 or EventID=1119) and TimeCreated[timediff(@SystemTime) <= {ThreatHistory.Days * 86_400_000L}]]]", TimeSpan.FromDays(ThreatHistory.Days));
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                {
                    var p = e.Properties;
                    string? Prop(int i) => p.Count > i ? p[i].Value?.ToString() : null;
                    rows.Add(new DefenderEventRow(e.Id, (e.TimeCreated ?? now).ToUniversalTime(), Prop(6), Prop(7)));
                }
            s.Threats = ThreatHistory.FromEvents(rows, now);
            s.ThreatSource = "eventlog";
        }
        catch (Exception ex) { s.ThreatNote += " | event log: " + ex.Message; }
    }

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static IReadOnlyList<SuspiciousStartup> ReadSuspiciousStartups()
    {
        Activity.ReadingRegistry("HKCU and HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "(programs that start from temporary or shared folders)");
        var temp = Path.GetFullPath(Path.GetTempPath());
        var appData
 = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
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
