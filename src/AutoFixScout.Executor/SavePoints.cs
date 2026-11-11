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
internal static class SavePoints
{
    // Exit codes the app understands (also in FixActions).
    public const int Ok = 0, Failed = 1, NothingToRollBackTo = 3, ChangedButUndone = 4, RestoreFailed = 6;

    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFixScout", "savepoints");

    public static int Rollback(string instanceId, string savePointId)
    {
        var device = DriverStore.Read(instanceId);
        if (device?.InfName is null || device.Version is null) return Failed;
        var previous = DriverStore.FindPrevious(device);
        if (previous is null) return NothingToRollBackTo;

        // 1. Save point. If this fails nothing has changed, so stop.
        var dir = Path.Combine(EnsureRoot(), savePointId);
        Directory.CreateDirectory(Path.Combine(dir, "driver"));
        if (Program.Pnputil("/export-driver", device.InfName, Path.Combine(dir, "driver")) != 0) return Failed;
        var exported = Directory.EnumerateFiles(Path.Combine(dir, "driver"), "*.inf", SearchOption.AllDirectories).FirstOrDefault();
        if (exported is null) return Failed;
        var sp = new SavePoint(savePointId, instanceId, previous.MatchedHardwareId, device.InfName, device.Version, exported, TryCreateRestorePoint());
        WriteManifest(dir, sp);

        // 2. Change, 3. post-check, 4. undo on any failure.
        try
        {
            Install(previous.MatchedHardwareId, previous.InfPath);
            if (PostCheck(instanceId, previous.Version.ToString())) return Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        return Restore(sp) ? ChangedButUndone : RestoreFailed;
    }

    public static int Restore(string savePointId)
    {
        var manifest = Path.Combine(Root, savePointId, "savepoint.json");
        // Only trust a manifest an administrator wrote; a standard user could otherwise plant one.
        if (!File.Exists(manifest) || !IsAdminOwned(manifest)) return Failed;
        var sp = JsonSerializer.Deserialize<SavePoint>(File.ReadAllText(manifest));
        if (sp is null || sp.Id != savePointId || !Program.IsValidInstanceId(sp.InstanceId)) return Failed;
        return Restore(sp) ? Ok : RestoreFailed;
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
            return PostCheck(sp.InstanceId, sp.Version);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return false; }
    }

    /// <summary>Spec 10 post-check: device present, no error code, and the driver we expect.</summary>
    private static bool PostCheck(string instanceId, string expectedVersion)
    {
        for (var i = 0; i < 15; i++)
        {
            var d = DriverStore.Read(instanceId);
            // Compare parsed versions: WMI and the INF may format the same version differently ("10.0.01" vs "10.0.1").
            if (d is { ProblemCode: 0 } && Version.TryParse(d.Version, out var actual) && Version.TryParse(expectedVersion, out var expected)
                && actual == expected) return true;
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
