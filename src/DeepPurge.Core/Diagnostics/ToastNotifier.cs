using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DeepPurge.Core.Diagnostics;

public static class ToastNotifier
{
    private const string AppId = "DeepPurge";

    public static void Show(string title, string body)
    {
        try
        {
            var xml = $@"<toast>
  <visual>
    <binding template='ToastGeneric'>
      <text>{EscapeXml(title)}</text>
      <text>{EscapeXml(body)}</text>
    </binding>
  </visual>
</toast>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc);
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch (Exception ex)
        {
            Log.Warn($"Toast notification failed: {ex.Message}");
        }
    }

    public static void ShowCleaningSummary(string operation, long bytesFreed, int itemCount, bool dryRun)
    {
        var prefix = dryRun ? "[Dry Run] " : "";
        var freed = bytesFreed > 0 ? $" — freed {FormatBytes(bytesFreed)}" : "";
        Show($"{prefix}DeepPurge: {operation}", $"{itemCount} items processed{freed}");
    }

    private static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:F1} {u[i]}";
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
