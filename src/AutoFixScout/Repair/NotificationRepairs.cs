using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>
/// Notification settings of the current user. Findings from this machine's registry (Windows 11 24H2): the global toggle is
/// HKCU\...\PushNotifications ToastEnabled (absent means on); Do not disturb is HKCU\...\Notifications\Settings
/// NOC_GLOBAL_SETTING_TOASTS_ENABLED = 0 (absent means off); per-app choices are subkeys of Notifications\Settings with
/// Enabled and ShowBanner values (absent means on). The QuietHours key holds only a first-run timer here, so it is not read.
/// </summary>
public static class NotificationRepairs
{
    public const string PushKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    public const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    /// <summary>shell.notifications_on: sets only ToastEnabled=1; per-app choices and Do not disturb are never touched.</summary>
    public static (bool Ok, string Detail) NotificationsOn() => NotificationsOn(Registry.CurrentUser, broadcast: true);

    internal static (bool Ok, string Detail) NotificationsOn(RegistryKey hkcu, bool broadcast)
    {
        using var k = hkcu.CreateSubKey(PushKey, true);
        var before = k.GetValue("ToastEnabled");
        var saved = UserRepairs.Backup("notifications", before is null ? "ToastEnabled=(absent)\n" : $"ToastEnabled={before}\n");
        k.SetValue("ToastEnabled", 1, RegistryValueKind.DWord);
        if (broadcast)
        {
            try { SendMessageTimeout((IntPtr)0xffff, 0x1A, UIntPtr.Zero, "Notifications", 2, 2000, out _); } catch { /* best effort */ }
        }
        var ok = k.GetValue("ToastEnabled") is int v && v == 1;
        return (ok, $"ToastEnabled was {before ?? "absent"}, now 1; previous value saved to {saved}");
    }
}
