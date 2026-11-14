using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AutoFixScout.UI;

/// <summary>The weekly check-up notification. Same AppUserModelID as the Start menu shortcut (and the Wi-Fi watcher). Fails silently.</summary>
internal static class WeeklyToast
{
    public const string AppId = "ShRike3601.AutoFixScout";

    public static void Show(string title, string body)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text>" + Esc(title) + "</text><text>" + Esc(body) +
                "</text></binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(xml));
        }
        catch (Exception) { /* a missing shortcut or a blocked toast must not matter */ }
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
