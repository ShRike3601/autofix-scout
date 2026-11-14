using System.Buffers.Binary;
using AutoFixScout.Probes;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>
/// Apps that start when the person signs in, and the switch "Startup apps" / Task Manager keep for them in
/// ...\Explorer\StartupApproved\{Run,Run32,StartupFolder}. A value there is 12 bytes: the first byte is even (0x02,
/// 0x06) for enabled and odd (0x03) for disabled; bytes 4-11 are the FILETIME the entry was disabled. No value means
/// enabled. startup.disable writes exactly what Task Manager writes, so Task Manager can switch the entry on again.
/// Only the person's own entries (HKCU Run, their Startup folder) are changed; machine-wide ones need admin and are
/// only named.
/// </summary>
public static class StartupApps
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    public const int MaxOffered = 5;

    // ---- StartupApproved values (pure) ----

    /// <summary>Missing or empty = enabled (Windows' default). Odd first byte = disabled.</summary>
    public static (bool Enabled, DateTime? DisabledAtUtc) ParseApproved(byte[]? value)
    {
        if (value is not { Length: > 0 }) return (true, null);
        if ((value[0] & 1) == 0) return (true, null);
        DateTime? when = null;
        if (value.Length >= 12)
        {
            var ft = BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(4, 8));
            if (ft > 0 && ft <= DateTime.MaxValue.ToFileTimeUtc()) when = DateTime.FromFileTimeUtc(ft);
        }
        return (false, when);
    }

    /// <summary>The 12 bytes Task Manager writes when an entry is disabled: 03 00 00 00 + FILETIME (little-endian).</summary>
    public static byte[] DisabledValue(DateTime utc)
    {
        var b = new byte[12];
        b[0] = 0x03;
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4, 8), utc.ToUniversalTime().ToFileTimeUtc());
        return b;
    }

    /// <summary>The 12 bytes Task Manager writes when an entry is switched on again: 02 00 00 00 and no time.</summary>
    public static byte[] EnabledValue() => new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    // ---- what may be offered (pure) ----

    private static readonly string[] SecurityWords =
    {
        "defender", "securityhealth", "windows security", "avast", "avg", "norton", "symantec", "mcafee", "kaspersky", "bitdefender",
        "eset", "egui", "malwarebytes", "mbam", "sophos", "trend micro", "webroot", "avira", "f-secure", "panda", "gdata", "g data",
        "comodo", "emsisoft", "crowdstrike", "sentinel", "cylance", "carbon black", "antivirus", "anti-virus", "firewall",
    };

    /// <summary>Launchers and updaters that rarely need to run right after sign-in, best first.</summary>
    private static readonly string[] KnownOptional =
    {
        "steam", "discord", "spotify", "epic", "battle.net", "origin", "ea desktop", "eadesktop", "ubisoft", "uplay", "gog galaxy",
        "riot", "rockstar", "xbox", "teams", "skype", "zoom", "adobe", "creative cloud", "ccxprocess", "acrobat", "dropbox",
        "google drive", "itunes", "icloud", "whatsapp", "telegram", "slack", "opera", "brave", "edge", "chrome", "update", "updater",
    };

    /// <summary>Program path from a Run command ("C:\x\a.exe" -args, or C:\x\a.exe -args), environment variables expanded.</summary>
    public static string ExecutablePath(string command)
    {
        var c = command.Trim();
        string path;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            path = end > 0 ? c[1..end] : c.Trim('"');
        }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? c[..(exe + 4)] : c.Split(' ', 2)[0];
        }
        return Environment.ExpandEnvironmentVariables(path);
    }

    /// <summary>Windows' own entries, security software and OneDrive are never offered.</summary>
    public static bool IsProtected(StartupEntry e, string windowsDir)
    {
        var name = e.Name;
        if (name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase)) return true;
        var all = (name + " " + e.Command).ToLowerInvariant();
        if (all.Contains("onedrive")) return true;
        if (SecurityWords.Any(w => System.Text.RegularExpressions.Regex.IsMatch(all, $@"(^|[^a-z]){System.Text.RegularExpressions.Regex.Escape(w)}"))) return true;
        var path = ExecutablePath(e.Command);
        var win = windowsDir.TrimEnd('\\') + "\\";
        if (path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return true;
        // An unrooted program name (rundll32, ctfmon) is found by Windows in its own folders.
        if (!Path.IsPathRooted(path) && path.Length > 0) return true;
        return false;
    }

    /// <summary>Only entries in the person's own Run key or Startup folder can be switched off without admin.</summary>
    public static bool IsUserEntry(StartupEntry e) => e.Scope is StartupScope.UserRun or StartupScope.UserFolder;

    private static int Rank(StartupEntry e)
    {
        var all = (e.Name + " " + Path.GetFileName(ExecutablePath(e.Command))).ToLowerInvariant();
        var i = Array.FindIndex(KnownOptional, all.Contains);
        return i < 0 ? KnownOptional.Length : i;
    }

    /// <summary>
    /// Enabled, not protected, the person's own. Entries that Windows measured as slowing start-up come first, the largest
    /// delay first (see <see cref="StartupSlowMatch"/>); then known launchers and updaters; at most <see cref="MaxOffered"/>.
    /// </summary>
    public static IReadOnlyList<StartupEntry> Offerable(IEnumerable<StartupEntry> entries, string windowsDir, IEnumerable<SlowStartupItem>? slow = null)
    {
        var pool = entries.Where(e => e.Enabled && IsUserEntry(e) && !IsProtected(e, windowsDir)).ToList();
        var delays = new Dictionary<StartupEntry, long>();
        foreach (var m in StartupSlowMatch.Match(slow, pool)) delays.TryAdd(m.Entry, m.Ms);
        return pool.OrderBy(e => delays.ContainsKey(e) ? 0 : 1).ThenByDescending(e => delays.TryGetValue(e, out var ms) ? ms : 0)
            .ThenBy(Rank).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxOffered).ToList();
    }

    /// <summary>Enabled machine-wide entries that are not protected: switching them off needs admin, so they are only named.</summary>
    public static IReadOnlyList<StartupEntry> MachineWide(IEnumerable<StartupEntry> entries, string windowsDir) =>
        entries.Where(e => e.Enabled && !IsUserEntry(e) && !IsProtected(e, windowsDir)).ToList();

    /// <summary>The name a person recognises: the Run value name, or the shortcut name without ".lnk".</summary>
    public static string DisplayName(StartupEntry e) =>
        e.Scope is StartupScope.UserFolder or StartupScope.CommonFolder ? Path.GetFileNameWithoutExtension(e.Name) : e.Name;

    // ---- reading (registry, read-only) ----

    public static List<StartupEntry> ReadAll()
    {
        var list = new List<StartupEntry>();
        Activity.Report(ActivityKind.Registry, "Reading the Run keys (HKCU and HKLM) and the Startup folders for programs that start with Windows");
        ReadRun(list, Registry.CurrentUser, RunKey, "Run", StartupScope.UserRun);

        ReadRun(list, Registry.LocalMachine, RunKey, "Run", StartupScope.MachineRun);
        ReadRun(list, Registry.LocalMachine, Run32Key, "Run32", StartupScope.MachineRun32);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, StartupScope.UserFolder);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, StartupScope.CommonFolder);
        return list;
    }

    private static void ReadRun(List<StartupEntry> list, RegistryKey hive, string path, string approvedName, StartupScope scope)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null) return;
        using var approved = hive.OpenSubKey(ApprovedKey + approvedName);
        foreach (var v in key.GetValueNames().Where(v => v.Length > 0))
        {
            var cmd = key.GetValue(v, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            list.Add(new StartupEntry(v, cmd, scope, ParseApproved(approved?.GetValue(v) as byte[]).Enabled));
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string dir, RegistryKey hive, StartupScope scope)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        using var approved = hive.OpenSubKey(ApprovedKey + "StartupFolder");
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new StartupEntry(name, ShortcutTarget(file) ?? file, scope, ParseApproved(approved?.GetValue(name) as byte[]).Enabled));
        }
    }

    /// <summary>A .lnk file's target through the Windows shell; null when it cannot be read.</summary>
    private static string? ShortcutTarget(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            try
            {
                dynamic link = shell.CreateShortcut(file);
                string target = link.TargetPath;
                string args = link.Arguments;
                return string.IsNullOrEmpty(target) ? null : $"\"{target}\" {args}".Trim();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception) { return null; }
    }

    // ---- startup.disable (non-admin) ----

    /// <summary>Shape check before the lookup: a registry value or file name, no path, no control characters.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 260 && name.Trim().Length > 0
        && !name.Any(c => char.IsControl(c) || c is '\\' or '/' or '"' or '*' or '?' or '<' or '>' or '|');

    /// <summary>
    /// Switches off one entry of the person's own Run key or Startup folder, exactly as Task Manager does. Anything
    /// else (a machine-wide entry, a name that does not exist) is refused and nothing is written.
    /// </summary>
    public static (bool Ok, string Detail) Disable(string name)
    {
        if (!IsValidName(name)) return (false, "startup.disable: invalid name, nothing changed");
        string approvedName;
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            if (run?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true) approvedName = "Run";
            else
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var inFolder = !string.IsNullOrEmpty(dir) && Directory.Exists(dir)
                    && Directory.EnumerateFiles(dir).Select(Path.GetFileName).Contains(name, StringComparer.OrdinalIgnoreCase);
                if (!inFolder) return (false, "startup.disable: no such entry in the user's Run key or Startup folder, nothing changed");
                approvedName = "StartupFolder";
            }
        }
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey + approvedName, writable: true);
        var before = approved.GetValue(name) as byte[];
        // Keep the exact spelling of an existing value so Task Manager sees one entry, not two.
        var valueName = approved.GetValueNames().FirstOrDefault(v => v.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        approved.SetValue(valueName, DisabledValue(DateTime.UtcNow), RegistryValueKind.Binary);
        var prior = before is null ? "no value (enabled)" : Convert.ToHexString(before);
        return (true, $"startup entry '{name}' disabled in StartupApproved\\{approvedName}; before: {prior}");
    }

    /// <summary>
    /// Switches an entry that startup.disable turned off back on, exactly as Task Manager does. Only the two places
    /// startup.disable writes to are accepted, and only a value that exists is changed.
    /// </summary>
    public static (bool Ok, string Detail) Enable(string name, string approvedName)
    {
        if (!IsValidName(name) || approvedName is not ("Run" or "StartupFolder")) return (false, "startup.enable: invalid entry, nothing changed");
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey + approvedName, writable: true);
        var valueName = approved?.GetValueNames().FirstOrDefault(v => v.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (approved is null || valueName is null) return (true, $"startup entry '{name}' is already enabled");
        approved.SetValue(valueName, EnabledValue(), RegistryValueKind.Binary);
        return (true, $"startup entry '{name}' enabled in StartupApproved\\{approvedName}");
    }
}
