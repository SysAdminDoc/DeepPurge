using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Shell;

public static class ShellExtensionRegistrar
{
    private const string MenuKeyPath = @"Software\Classes\exefile\shell\DeepPurge";
    private const string CommandKeyPath = @"Software\Classes\exefile\shell\DeepPurge\command";

    public static bool Register(string deeppurgePath)
    {
        if (string.IsNullOrWhiteSpace(deeppurgePath) || !File.Exists(deeppurgePath))
            return false;

        try
        {
            using var menuKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(MenuKeyPath);
            if (menuKey == null) return false;

            menuKey.SetValue("", "Uninstall with DeepPurge");
            menuKey.SetValue("Icon", deeppurgePath);

            using var cmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(CommandKeyPath);
            if (cmdKey == null) return false;

            cmdKey.SetValue("", $"\"{deeppurgePath}\" --target \"%1\"");
            Log.Info($"Shell extension registered: {deeppurgePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to register shell extension: {ex.Message}", ex);
            return false;
        }
    }

    public static bool Unregister()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(MenuKeyPath, throwOnMissingSubKey: false);
            Log.Info("Shell extension unregistered");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to unregister shell extension: {ex.Message}", ex);
            return false;
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(MenuKeyPath);
            return key != null;
        }
        catch { return false; }
    }
}
