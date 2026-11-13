using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AutoFixScout.Watching;

/// <summary>
/// The "Wi-Fi reconnected" toast. No network name, at most one per 10 minutes, off when the person switched it off in
/// wifi-watch-settings.json. The AppUserModelID is the one the installer sets on the Start menu shortcut. Fails silently.
/// </summary>
internal static class WifiToast
{
    public const string AppId = "ShRike3601.AutoFixScout";

    public static void ShowReconnected(WifiWatchSettings settings, ToastGate gate)
    {
        // Off the caller's thread: the caller holds the decision lock.
        _ = Task.Run(() =>
        {
            try
            {
                if (!settings.Notify || !gate.TryAcquire(DateTime.UtcNow)) return;
                var xml = new XmlDocument();
                xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\">" +
                    "<text>Wi-Fi reconnected</text><text>AutoFix Scout reconnected your Wi-Fi after it dropped.</text>" +
                    "</binding></visual></toast>");
                ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(xml));
            }
            catch (Exception) { /* a missing shortcut or a blocked toast must not stop the watcher */ }
        });
    }
}
