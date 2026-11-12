using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Repair;

namespace AutoFixScout.Triage;

/// <summary>An antivirus or firewall product as Windows Security Center lists it (root\SecurityCenter2).</summary>
public sealed record SecurityProduct(string Name, int ProductState, string? Path = null)
{
    public bool Enabled => SecurityTriage.IsEnabled(ProductState);
    public bool UpToDate => SecurityTriage.IsUpToDate(ProductState);
    public bool IsMicrosoft => Name.Contains("Defender", StringComparison.OrdinalIgnoreCase)
                               || Name.Contains("Windows Firewall", StringComparison.OrdinalIgnoreCase)
                               || (Path ?? "").StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A program started from a Run key whose file sits in a temporary or shared folder and has a random-looking name.</summary>
public sealed record SuspiciousStartup(string Location, string Name, string File);

/// <summary>
/// probe.security: antivirus, firewall, User Account Control, SmartScreen, hosts file, proxy and suspicious startup
/// entries. Tri.Unknown and null mean "could not be read"; the triage never reads that as a problem or as healthy.
/// </summary>
public sealed class SecurityState
{
    /// <summary>When the check ran; signature age is measured against it.</summary>
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null = Windows Security Center could not be read.</summary>
    public IReadOnlyList<SecurityProduct>? Antivirus { get; set; }
    public IReadOnlyList<SecurityProduct> Firewalls { get; set; } = new List<SecurityProduct>();

    /// <summary>Microsoft Defender details (MSFT_MpComputerStatus); Unknown/null when Defender is not active or access was denied.</summary>
    public Tri DefenderRealTime { get; set; } = Tri.Unknown;
    public DateTime? DefenderSignaturesUtc { get; set; }
    public int? DefenderQuickScanAgeDays { get; set; }
    public string? DefenderNote { get; set; }

    /// <summary>Windows Defender Firewall per profile ("Domain", "Private", "Public"): Yes = on, No = off.</summary>
    public IReadOnlyDictionary<string, Tri> FirewallProfiles { get; set; } = new Dictionary<string, Tri>();
    public UpdateServiceInfo? FirewallService { get; set; }

    /// <summary>EnableLUA: Yes = User Account Control on.</summary>
    public Tri UacEnabled { get; set; } = Tri.Unknown;
    /// <summary>ConsentPromptBehaviorAdmin; 0 = elevate without asking.</summary>
    public int? UacAdminPrompt { get; set; }

    /// <summary>Explorer\SmartScreenEnabled ("Off", "Warn", "RequireAdmin"; null when not set, which is Windows' default: on).</summary>
    public string? SmartScreenSetting { get; set; }
    /// <summary>Policy EnableSmartScreen (0 = switched off by policy); null when no policy is set.</summary>
    public int? SmartScreenPolicy { get; set; }

    /// <summary>Hosts file and proxy, read by <see cref="RealHealthProbe.ReadHostsAndProxy"/>.</summary>
    public HealthState Network { get; set; } = new();
    public IReadOnlyList<SuspiciousStartup> SuspiciousStartups { get; set; } = new List<SuspiciousStartup>();

    /// <summary>Hijack-type browser policies found under HKLM and HKCU (read only).</summary>
    internal IReadOnlyList<BrowserPolicies.Flag> BrowserPolicyFlags { get; set; } = new List<BrowserPolicies.Flag>();
    /// <summary>The computer belongs to a domain or Azure AD: browser policies are then expected, and are not removed.</summary>
    public bool ManagedPc { get; set; }

    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

/// <summary>
/// "Security and unwanted software". Pure logic. AutoFix Scout never changes Microsoft Defender's own settings (tamper
/// protection blocks that anyway) and never deletes a program: the person switches protection and startup entries on
/// or off in Windows, and the admin steps (firewall, User Account Control) save the previous values first.
/// </summary>
public static class SecurityTriage
{
    public const int SignatureMaxAgeDays = 7;

    /// <summary>Security Center productState, bits 12-15: 1 = on.</summary>
    public static bool IsEnabled(int state) => ((state >> 12) & 0xF) == 1;

    /// <summary>Security Center productState, bits 4-7: 0 = definitions up to date.</summary>
    public static bool IsUpToDate(int state) => ((state >> 4) & 0xF) == 0;

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private static Rung Admin(string id, string action, string say, string arg = "system") =>
        new(id, action, 1, say + " Windows will ask for your permission.", arg);

    private static readonly FindingCode[] NetworkCodes =
        { FindingCode.HealthHostsHijacked, FindingCode.HealthDeadProxy, FindingCode.HealthDeadSystemProxy };

    private static readonly string[] NetworkActions = { "network.clean_hosts", "network.reset_user_proxy", "network.reset_winhttp_proxy" };

    public static IReadOnlyList<SecurityProduct> ActiveAntivirus(SecurityState s) => s.Antivirus?.Where(a => a.Enabled).ToList() ?? new List<SecurityProduct>();

    /// <summary>Microsoft Defender is the antivirus that protects this computer (or would, once switched back on).</summary>
    public static bool DefenderInCharge(SecurityState s)
    {
        var active = ActiveAntivirus(s);
        if (active.Count > 0) return active.Any(a => a.IsMicrosoft);
        return s.Antivirus?.Any(a => a.IsMicrosoft) == true || s.DefenderRealTime != Tri.Unknown;
    }

    private static bool ThirdPartyFirewall(SecurityState s) => s.Firewalls.Any(f => f.Enabled && !f.IsMicrosoft);

    public static IReadOnlyList<string> FirewallOffProfiles(SecurityState s) =>
        ThirdPartyFirewall(s) ? Array.Empty<string>() : s.FirewallProfiles.Where(p => p.Value == Tri.No).Select(p => p.Key).ToList();

    private static bool FirewallServiceFault(SecurityState s) =>
        s.FirewallService is { } svc && ServiceDefaults.IsFault(svc.Name, svc.Status, svc.StartType) && !ThirdPartyFirewall(s);

    public static bool SmartScreenOff(SecurityState s) =>
        s.SmartScreenPolicy == 0 || string.Equals(s.SmartScreenSetting, "Off", StringComparison.OrdinalIgnoreCase);

    private static bool NothingRead(SecurityState s) =>
        s.Antivirus is null && s.FirewallProfiles.Count == 0 && s.UacEnabled == Tri.Unknown && s.FirewallService is null;

    public static IReadOnlyList<Finding> Evaluate(SecurityState s)
    {
        if (s.ProbeErrors.Count > 0 && NothingRead(s))
            return new[] { F(FindingCode.ProbeIncomplete, "AutoFix Scout could not complete the check.", string.Join("; ", s.ProbeErrors)) };
        var f = new List<Finding>();
        var active = ActiveAntivirus(s);
        if (s.Antivirus is not null && active.Count == 0)
        {
            if (DefenderInCharge(s))
                f.Add(F(FindingCode.SecurityRealTimeOff,
                    "Real-time protection in Microsoft Defender is turned off, and no other antivirus program is active. This computer is not protected against viruses.",
                    $"products: {Products(s.Antivirus)}; real-time {s.DefenderRealTime}"));
            else
                f.Add(F(FindingCode.SecurityNoAntivirus, "No antivirus program is active on this computer.", $"products: {Products(s.Antivirus)}"));
        }
        else if (active.Any(a => a.IsMicrosoft) && s.DefenderRealTime == Tri.No)
            f.Add(F(FindingCode.SecurityRealTimeOff, "Real-time protection in Microsoft Defender is turned off.", "MSFT_MpComputerStatus RealTimeProtectionEnabled=False"));

        if (SignaturesOld(s) is { } old)
            f.Add(F(FindingCode.SecuritySignaturesOld,
                $"The virus definitions of \"{old.Name}\" are out of date, so new threats may not be recognized.", old.Detail));

        var offProfiles = FirewallOffProfiles(s);
        if (offProfiles.Count > 0 || FirewallServiceFault(s))
            f.Add(F(FindingCode.SecurityFirewallOff,
                offProfiles.Count > 0
                    ? $"Windows Defender Firewall is turned off for {ProfileList(offProfiles)}."
                    : "The \"Windows Defender Firewall\" service is switched off or stopped, so the firewall does not protect this computer.",
                $"profiles: {string.Join(", ", s.FirewallProfiles.Select(p => $"{p.Key}={p.Value}"))}; mpssvc {s.FirewallService?.Status}/{s.FirewallService?.StartType}"));

        if (s.UacEnabled == Tri.No)
            f.Add(F(FindingCode.SecurityUacOff, "User Account Control is turned off. Programs can change Windows without asking for your permission.",
                $"EnableLUA=0, ConsentPromptBehaviorAdmin={s.UacAdminPrompt}"));
        else if (s.UacEnabled == Tri.Yes && s.UacAdminPrompt == 0)
            f.Add(F(FindingCode.SecurityUacOff, "User Account Control is set to never ask for permission. Programs can change Windows without your knowledge.",
                "EnableLUA=1, ConsentPromptBehaviorAdmin=0"));

        if (SmartScreenOff(s))
            f.Add(F(FindingCode.SecuritySmartScreenOff, "Microsoft Defender SmartScreen is turned off, so Windows does not warn you about unknown or harmful downloads.",
                $"SmartScreenEnabled={s.SmartScreenSetting ?? "(not set)"}, policy EnableSmartScreen={s.SmartScreenPolicy?.ToString() ?? "(not set)"}"));

        f.AddRange(HealthTriage.Evaluate(s.Network).Where(x => NetworkCodes.Contains(x.Code)));

        if (s.SuspiciousStartups.Count > 0)
            f.Add(F(FindingCode.SecuritySuspiciousStartup,
                $"A program that starts with Windows runs from a temporary or shared folder and has a random-looking name: {Names(s.SuspiciousStartups)}. It may be unwanted software.",
                string.Join("; ", s.SuspiciousStartups.Select(e => $"{e.Location} {e.Name} -> {e.File}"))));

        if (s.BrowserPolicyFlags.Count > 0)
        {
            var browsers = string.Join(", ", s.BrowserPolicyFlags.Select(x => x.Browser).Distinct());
            if (s.ManagedPc)
                f.Add(F(FindingCode.SecurityBrowserPolicyManaged,
                    $"This computer is managed by an organization, and the settings of {browsers} include rules set by that organization. This is normal on a work or school computer, so AutoFix Scout leaves them unchanged.",
                    BrowserPolicies.Summary(s.BrowserPolicyFlags)));
            else
                f.Add(F(FindingCode.SecurityBrowserPolicy,
                    $"A program has taken control of browser settings in {browsers}, such as the home page, the search engine, the proxy server or added extensions. The browser may show \"Managed by your organization\".",
                    BrowserPolicies.Summary(s.BrowserPolicyFlags)));
        }

        if (f.Count > 0) return f;
        return new[] { F(FindingCode.LooksNormal,
            s.ProbeErrors.Count > 0
                ? "No security problem was found in the parts that could be checked. AutoFix Scout can still scan the computer for unwanted software."
                : "No security problem was found. AutoFix Scout can still scan the computer for unwanted software.",
            $"antivirus: {Products(s.Antivirus)}; errors: {string.Join("; ", s.ProbeErrors)}") };
    }

    /// <summary>The active antivirus whose definitions are too old: Defender by its own date, others by Security Center's flag.</summary>
    public static (string Name, string Detail)? SignaturesOld(SecurityState s)
    {
        var candidates = ActiveAntivirus(s);
        if (candidates.Count == 0 && DefenderInCharge(s))
            candidates = s.Antivirus?.Where(a => a.IsMicrosoft).ToList() ?? new List<SecurityProduct>();
        foreach (var a in candidates)
        {
            if (a.IsMicrosoft && s.DefenderSignaturesUtc is { } updated)
            {
                var age = (s.CheckedUtc - updated).TotalDays;
                if (age > SignatureMaxAgeDays) return ("Microsoft Defender", $"signatures updated {updated:yyyy-MM-dd} ({age:0} days)");
            }
            else if (!a.UpToDate)
                return (a.IsMicrosoft ? "Microsoft Defender" : a.Name, $"{a.Name} productState 0x{a.ProductState:x}: not up to date");
        }
        return null;
    }

    public static IReadOnlyList<Rung> Ladder(SecurityState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var r = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return r;
        var defender = DefenderInCharge(s);
        var other = ActiveAntivirus(s).FirstOrDefault(a => !a.IsMicrosoft);

        if (codes.Contains(FindingCode.SecurityRealTimeOff) || codes.Contains(FindingCode.SecurityNoAntivirus))
            r.Add(new("turn_on_realtime", "ui.open_settings_page", 0,
                "AutoFix Scout will open \"Windows Security\". Please select \"Virus & threat protection\", then \"Manage settings\", and turn on \"Real-time protection\".",
                "windowsdefender"));

        if (codes.Contains(FindingCode.SecuritySignaturesOld))
        {
            if (SignaturesOld(s) is { } old && old.Name != "Microsoft Defender")
                r.Add(new("update_other_antivirus", "ui.human_step", 0,
                    $"Please open your antivirus program \"{old.Name}\" and select its option to update the virus definitions."));
            else
                r.Add(new("update_signatures", "security.update_signatures", 0,
                    "AutoFix Scout can ask Microsoft Defender to download its latest virus definitions. This needs an internet connection and can take a few minutes."));
        }

        if (codes.Contains(FindingCode.SecurityFirewallOff))
        {
            if (FirewallServiceFault(s))
                r.Add(Admin("restore_mpssvc", "service.restore_default",
                    "AutoFix Scout can set the \"Windows Defender Firewall\" service back to the way Windows normally runs it.", "mpssvc"));
            if (FirewallOffProfiles(s).Count > 0)
                r.Add(Admin("firewall_on", "security.firewall_on",
                    "AutoFix Scout can turn Windows Defender Firewall back on for all networks. The previous settings are saved first."));
        }

        if (codes.Contains(FindingCode.SecuritySmartScreenOff))
            r.Add(new("turn_on_smartscreen", "ui.open_settings_page", 0,
                "AutoFix Scout will open \"App & browser control\" in Windows Security. Please select \"Reputation-based protection settings\" and turn on \"Check apps and files\" and \"SmartScreen for Microsoft Edge\".",
                "windowsdefender_appbrowser"));

        if (codes.Overlaps(NetworkCodes))
            r.AddRange(HealthTriage.Ladder(s.Network, findings.Where(x => NetworkCodes.Contains(x.Code)).ToList())
                .Where(x => NetworkActions.Contains(x.ActionId)));

        if (codes.Contains(FindingCode.SecuritySuspiciousStartup))
            r.Add(new("review_startup", "ui.open_settings_page", 0,
                $"AutoFix Scout will open \"Startup apps\". If you do not recognize {Names(s.SuspiciousStartups)}, please switch it off. Nothing is deleted, and you can switch it on again at any time.",
                "startupapps"));

        if (codes.Contains(FindingCode.SecurityBrowserPolicy))
        {
            if (s.BrowserPolicyFlags.Any(x => x.Hive == BrowserPolicies.HiveUser))
                r.Add(new("remove_browser_policies_user", "security.remove_browser_policies_user", 0,
                    "AutoFix Scout can remove the browser rules that were added to your own Windows account (home page, search engine, proxy server and extensions). The previous values are saved first."));
            if (s.BrowserPolicyFlags.Any(x => x.Hive == BrowserPolicies.HiveMachine))
                r.Add(Admin("remove_browser_policies", "security.remove_browser_policies",
                    "AutoFix Scout can remove the browser rules that were added for all users of this computer (home page, search engine, proxy server and extensions). The previous values are saved first."));
            r.Add(new("check_browser_policy", "ui.human_step", 0,
                "Please close all browser windows and open the browser again. Then type chrome://policy (Chrome), edge://policy (Edge) or about:policies (Firefox) in the address bar and check that no rule is listed. " +
                "If a rule is still listed, select \"Reload policies\" on that page."));
        }

        if (defender)
            r.Add(new("quick_scan", "security.quick_scan", 0,
                "AutoFix Scout can ask Microsoft Defender to run a quick scan for unwanted software. This takes a few minutes, and you can keep working."));
        else if (other is not null)
            r.Add(new("scan_other_antivirus", "ui.human_step", 0, $"Please open your antivirus program \"{other.Name}\" and start a full scan."));

        if (codes.Contains(FindingCode.SecurityUacOff))
        {
            r.Add(Admin("uac_on", "security.uac_on",
                "AutoFix Scout can turn User Account Control back on with the Windows default settings. The previous settings are saved first. A restart is required before the change takes effect."));
            r.Add(new("restart_after_uac", "ui.human_step", 0,
                "Please save your work and restart the computer: select \"Start\", then \"Power\", then \"Restart\". After the restart, please open AutoFix Scout again."));
        }
        return r;
    }

    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        var codes = after.Select(x => x.Code).ToHashSet();
        if (codes.Contains(FindingCode.SecurityUacOff))
            return "User Account Control is turned back on only after a restart. Please restart the computer and run the check again.";
        if (codes.Contains(FindingCode.SecurityBrowserPolicy))
            return "Browser rules that come back after they were removed are put back by a program on this computer. A scan for unwanted software is the next step, and the person helping you can check the installed programs for ones you do not recognize.";
        if (codes.Contains(FindingCode.SecurityRealTimeOff) || codes.Contains(FindingCode.SecuritySmartScreenOff))
            return "If a protection setting cannot be turned on, or turns itself off again, it may be managed by an organization or changed by unwanted software. " +
                   "The person helping you can run \"Microsoft Defender Offline scan\" from \"Windows Security\", \"Virus & threat protection\", \"Scan options\".";
        return "If unwanted software keeps returning, \"Microsoft Defender Offline scan\" can remove it before Windows starts. " +
               "The person helping you can start it in \"Windows Security\", \"Virus & threat protection\", \"Scan options\".";
    }

    /// <summary>The re-check after the steps: none of these may remain (startup entries are the person's choice, so they are not re-checked).</summary>
    public static readonly FindingCode[] Rechecked =
    {
        FindingCode.SecurityNoAntivirus, FindingCode.SecurityRealTimeOff, FindingCode.SecuritySignaturesOld, FindingCode.SecurityFirewallOff,
        FindingCode.SecuritySmartScreenOff, FindingCode.HealthHostsHijacked, FindingCode.HealthDeadProxy, FindingCode.HealthDeadSystemProxy,
        FindingCode.SecurityBrowserPolicy,
    };

    public static bool Resolved(IReadOnlyList<Finding> before, IReadOnlyList<Finding> after) =>
        !after.Any(a => a.Code == FindingCode.ProbeIncomplete) &&
        !before.Select(b => b.Code).Intersect(Rechecked).Intersect(after.Select(a => a.Code)).Any();

    private static string Products(IEnumerable<SecurityProduct>? p) =>
        p is null ? "unknown" : string.Join(", ", p.Select(a => $"{a.Name} 0x{a.ProductState:x}"));

    private static string Names(IEnumerable<SuspiciousStartup> e) => string.Join(", ", e.Take(3).Select(x => $"\"{x.Name}\""));

    private static string ProfileList(IReadOnlyList<string> profiles)
    {
        var names = profiles.Select(p => $"{p.ToLowerInvariant()} networks").ToList();
        return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    // ---- suspicious startup entries (heuristic; a finding only, nothing is removed automatically) ----

    /// <summary>The program file a Run value starts: the quoted path, or the text up to ".exe".</summary>
    public static string? ExecutableOf(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command.Split(' ', 2)[0];
    }

    /// <summary>
    /// True when the program sits in the temporary folder (or below it), directly in the roaming app-data folder, or in
    /// the shared Public folder (or below it), and its name looks random. Each part alone is common in normal software.
    /// </summary>
    public static bool IsSuspiciousStartup(string command, string temp, string appData, string publicDir)
    {
        if (ExecutableOf(command) is not { } file || file.Length == 0) return false;
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ""; }
        catch (Exception) { return false; }
        static string N(string p) => p.TrimEnd('\\', '/');
        var inTemp = temp.Length > 0 && (N(dir).Equals(N(temp), StringComparison.OrdinalIgnoreCase) || dir.StartsWith(N(temp) + "\\", StringComparison.OrdinalIgnoreCase));
        var inAppDataRoot = appData.Length > 0 && N(dir).Equals(N(appData), StringComparison.OrdinalIgnoreCase);
        var inPublic = publicDir.Length > 0 && (N(dir).Equals(N(publicDir), StringComparison.OrdinalIgnoreCase) || dir.StartsWith(N(publicDir) + "\\", StringComparison.OrdinalIgnoreCase));
        return (inTemp || inAppDataRoot || inPublic)
               && Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase)
               && IsRandomLookingName(Path.GetFileNameWithoutExtension(file));
    }

    /// <summary>"a8f3c91d", "xkqzvbtp", "r4nd0m7x" look random; "setup", "updater", "OneDriveSetup" do not.</summary>
    public static bool IsRandomLookingName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Length < 6 || !Regex.IsMatch(n, "^[a-z0-9_-]+$")) return false;
        var core = n.Replace("-", "").Replace("_", "");
        if (Regex.IsMatch(core, "^[0-9a-f]{8,}$") && core.Any(char.IsDigit)) return true;   // hex or GUID-like
        var switches = 0;
        for (var i = 1; i < core.Length; i++)
            if (char.IsDigit(core[i]) != char.IsDigit(core[i - 1])) switches++;
        if (switches >= 3 && core.Count(char.IsDigit) >= 2) return true;                   // letters and digits mixed
        var letters = core.Where(char.IsLetter).ToArray();
        if (letters.Length >= 6)
        {
            var vowels = letters.Count(c => "aeiouy".Contains(c));
            var longestConsonants = Regex.Matches(new string(letters), "[^aeiouy]+").Select(m => m.Length).DefaultIfEmpty(0).Max();
            if (vowels == 0 || longestConsonants >= 5) return true;
        }
        return false;
    }
}
