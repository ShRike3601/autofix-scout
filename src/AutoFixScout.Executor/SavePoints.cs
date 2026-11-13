using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AutoFixScout.Drivers;

namespace AutoFixScout.Executor;

/// <summary>What a save point remembers: enough to put the device's driver back exactly as it was.</summary>
internal sealed record SavePoint(string Id, string InstanceId, string HardwareId, string InfName, string Version, string ExportedInf, bool RestorePointCreated);

/// <summary>
/// Save points for T2 driver changes (spec 10): export the current package, change, post-check, auto-undo.
/// Stored where only administrators can write, because the elevated restore trusts what it reads there.
/// </summary>
public static class SavePoints
{
    // Exit codes the app understands (also in FixActions).
    public const int Ok = 0, Failed = 1, NothingToDo = 3, ChangedButUndone = 4, RestoreFailed = 6;

    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFixScout", "savepoints");

    /// <summary>A planned driver change: does the install and says which driver version should be running afterwards (null = any but the old one).</summary>
    public delegate Version? Plan();

    public static int Rollback(string instanceId, string savePointId) => Change(instanceId, savePointId, device =>
    {
        var previous = DriverStore.FindPrevious(device);
        if (previous is null) return null;
        return () => { Install(previous.MatchedHardwareId, previous.InfPath); return previous.Version; };
    });

    public static int InstallFromWindowsUpdate(string instanceId, string savePointId) => Change(instanceId, savePointId, device =>
    {
        // Searching is read-only, so it happens before the save point; "nothing offered" changes nothing.
        var update = WindowsUpdateDrivers.Find(device.HardwareIds);
        if (update is null) return null;
        return () => { WindowsUpdateDrivers.Install(update); return null; };
    });

    /// <summary>Spec 10 steps 1-9: save point, change, post-check, automatic undo on any failure.</summary>
    private static int Change(string instanceId, string savePointId, Func<DeviceDriver, Plan?> plan)
    {
        var device = DriverStore.Read(instanceId);
        if (device?.InfName is null || device.Version is null || !Version.TryParse(device.Version, out var before)) return Failed;
        var change = plan(device);
        if (change is null) return NothingToDo;

        // 1. Save point. If this fails nothing has changed, so stop.
        var root = EnsureRoot();
        Prune(root, DateTime.UtcNow, HasOpenMarker);
        var dir = Path.Combine(root, savePointId);
        Directory.CreateDirectory(Path.Combine(dir, "driver"));
        var exported = Program.Pnputil("/export-driver", device.InfName, Path.Combine(dir, "driver")) == 0
            ? Directory.EnumerateFiles(Path.Combine(dir, "driver"), "*.inf", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (exported is null) { Delete(root, savePointId); return Failed; }
        var sp = new SavePoint(savePointId, instanceId, RestoreHardwareId(device), device.InfName, device.Version, exported, TryCreateRestorePoint());
        WriteManifest(dir, sp);

        // 2. Change, 3. post-check, 4. undo on any failure.
        try
        {
            var expected = change();
            if (PostCheck(instanceId, v => expected is null ? v != before : v == expected)) return Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        return Settle(root, sp, Restore(sp)) == Ok ? ChangedButUndone : RestoreFailed;
    }

    /// <summary>
    /// After a restore: done means the save point is spent, so its folder goes. Failed means it's the helper's only way
    /// back, so it's flagged and never pruned (exit code 6).
    /// </summary>
    private static int Settle(string root, SavePoint sp, bool restored)
    {
        if (restored) { Delete(root, sp.Id); return Ok; }
        try { File.WriteAllText(Path.Combine(root, sp.Id, RestoreFailedFlag), DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        return RestoreFailed;
    }

    /// <summary>The most specific hardware ID the current driver's INF covers, so the restore force-installs it on this device.</summary>
    private static string RestoreHardwareId(DeviceDriver device)
    {
        var inf = Path.Combine(DriverStore.InfDirectory, device.InfName!);
        var text = File.Exists(inf) ? File.ReadAllText(inf) : "";
        return device.HardwareIds.FirstOrDefault(h => text.Contains(h, StringComparison.OrdinalIgnoreCase)) ?? device.HardwareIds[0];
    }

    public static int Restore(string savePointId) => Restore(Root, savePointId);

    public static int Restore(string root, string savePointId)
    {
        var manifest = Path.Combine(root, savePointId, "savepoint.json");
        // The manifest is written before any change, so without one nothing was changed: nothing to put back
        // (also the case after a restore that already succeeded and removed the folder).
        if (!File.Exists(manifest)) { Delete(root, savePointId); return NothingToDo; }
        // Only trust a manifest an administrator wrote; a standard user could otherwise plant one.
        if (!IsAdminOwned(manifest)) return Failed;
        var sp = JsonSerializer.Deserialize<SavePoint>(File.ReadAllText(manifest));
        if (sp is null || sp.Id != savePointId || !Program.IsValidInstanceId(sp.InstanceId)) return Failed;
        return Settle(root, sp, Restore(sp));
    }

    private static bool Restore(SavePoint sp)
    {
        // Prefer the copy still in Windows' own driver store; the export covers the case where it was removed.
        var published = Path.Combine(DriverStore.InfDirectory, sp.InfName);
        var source = File.Exists(published) ? published : sp.ExportedInf;
        if (source == sp.ExportedInf && !IsAdminOwned(source)) return false;
        try
        {
            Install(sp.HardwareId, source);
            var original = Version.Parse(sp.Version);
            return PostCheck(sp.InstanceId, v => v == original);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return false; }
    }

    /// <summary>Spec 10 post-check: device present, no error code, and the driver we expect.</summary>
    private static bool PostCheck(string instanceId, Func<Version, bool> isExpected)
    {
        for (var i = 0; i < 15; i++)
        {
            var d = DriverStore.Read(instanceId);
            // Compare parsed versions: WMI and the INF may format the same version differently ("10.0.01" vs "10.0.1").
            if (d is { ProblemCode: 0 } && Version.TryParse(d.Version, out var actual) && isExpected(actual)) return true;
            Thread.Sleep(1000); // PnP takes a moment to settle after an install
        }
        return false;
    }

    private const uint INSTALLFLAG_FORCE = 0x1, INSTALLFLAG_NONINTERACTIVE = 0x4;

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);

    /// <summary>Installs this package on devices with this hardware ID even if Windows ranks another higher (needed to go back a version).</summary>
    private static void Install(string hardwareId, string infPath)
    {
        // NONINTERACTIVE: fail instead of showing an "unsigned driver" prompt; signature checks stay Windows' job.
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out _))
            throw new InvalidOperationException($"UpdateDriverForPlugAndPlayDevices failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Best effort only (spec 10): System Restore may be off, and Windows allows about one per 24 hours.</summary>
    private static bool TryCreateRestorePoint()
    {
        try
        {
            using var sr = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            // 10 = DEVICE_DRIVER_INSTALL, 100 = BEGIN_SYSTEM_CHANGE (as Checkpoint-Computer does).
            var result = sr.InvokeMethod("CreateRestorePoint", new object[] { "AutoFix Scout driver change", 10, 100 });
            return result is uint code && code == 0;
        }
        catch (Exception) { return false; }
    }

    private static string EnsureRoot()
    {
        var root = new DirectoryInfo(Root);
        root.Create();
        // Admins and SYSTEM full control, users read-only, nothing inherited from ProgramData (which lets users create files).
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        acl.SetOwner(Admins);
        root.SetAccessControl(acl);
        return root.FullName;
    }

    // ---- retention ----
    // A restored save point is deleted at once (Settle). One the person kept ("it works") can't be deleted then without
    // another permission prompt, so it stays as a helper's way back and is pruned when the next save point is made.

    /// <summary>Written into a save point whose restore failed: kept for the helper, never pruned.</summary>
    public const string RestoreFailedFlag = "restore-failed";

    internal static readonly TimeSpan KeepSettledFor = TimeSpan.FromDays(30);

    /// <summary>
    /// Save points that may go: older than 30 days, not flagged restore-failed, and with no open marker in the app
    /// (an unconfirmed change the app will still offer to put back). Only our own 32-hex folders, never links.
    /// </summary>
    public static IReadOnlyList<string> Prunable(string root, DateTime nowUtc, Func<string, bool> hasOpenMarker) =>
        new DirectoryInfo(root).GetDirectories()
            .Where(d => Program.IsValidSavePointId(d.Name)
                        && !d.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && nowUtc - d.CreationTimeUtc > KeepSettledFor
                        && !File.Exists(Path.Combine(d.FullName, RestoreFailedFlag))
                        && !hasOpenMarker(d.Name))
            .Select(d => d.Name)
            .ToList();

    public static void Prune(string root, DateTime nowUtc, Func<string, bool> hasOpenMarker)
    {
        try { foreach (var id in Prunable(root, nowUtc, hasOpenMarker)) Delete(root, id); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); } // housekeeping never blocks a save point
    }

    /// <summary>Deletes one save point folder: a valid id, directly inside the save point root, not a link.</summary>
    public static bool Delete(string root, string id)
    {
        if (!Program.IsValidSavePointId(id)) return false;
        var dir = new DirectoryInfo(Path.Combine(Path.GetFullPath(root), id));
        if (!dir.Exists) return true;
        if (!string.Equals(dir.Parent?.FullName, Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        try
        {
            // Exported driver files can be read-only. Links inside aren't followed (enumeration skips them).
            foreach (var f in dir.EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (f.IsReadOnly) f.IsReadOnly = false;
            dir.Delete(recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(ex.Message); return false; }
    }

    /// <summary>
    /// Is the app still holding this save point open (%LOCALAPPDATA%\AutoFixScout\pending-savepoints\&lt;id&gt;)? The
    /// executor may run as another account than the app (a standard user typing an admin password), so every profile
    /// is checked, including MSIX-redirected copies (HANDOFF step 1). Can't tell = yes: keep it.
    /// </summary>
    internal static bool HasOpenMarker(string id)
    {
        try
        {
            var locals = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            using (var profiles = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                foreach (var sid in profiles?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var p = profiles!.OpenSubKey(sid);
                    if (p?.GetValue("ProfileImagePath") is string path)
                        locals.Add(Path.Combine(Environment.ExpandEnvironmentVariables(path), "AppData", "Local"));
                }
            foreach (var local in locals.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(local, "AutoFixScout", "pending-savepoints", id))) return true;
                var packages = Path.Combine(local, "Packages");
                if (Directory.Exists(packages) && Directory.EnumerateDirectories(packages).Any(p =>
                        File.Exists(Path.Combine(p, "LocalCache", "Local", "AutoFixScout", "pending-savepoints", id))))
                    return true;
            }
            return false;
        }
        catch (Exception) { return true; }
    }

    private static void WriteManifest(string dir, SavePoint sp)
    {
        var path = Path.Combine(dir, "savepoint.json");
        File.WriteAllText(path, JsonSerializer.Serialize(sp, new JsonSerializerOptions { WriteIndented = true }));
        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        acl.SetOwner(Admins);
        file.SetAccessControl(acl);
    }

    private static bool IsAdminOwned(string path)
    {
        var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        return owner == Admins || owner == SystemSid;
    }
}
