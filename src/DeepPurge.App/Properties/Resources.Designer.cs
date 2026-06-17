namespace DeepPurge.App.Properties;

using System.Globalization;
using System.Resources;

public static class Resources
{
    private static readonly ResourceManager _rm = new("DeepPurge.App.Properties.Resources", typeof(Resources).Assembly);

    public static string GetString(string name) => _rm.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    public static string AppTitle => GetString("AppTitle");
    public static string Nav_InstalledPrograms => GetString("Nav_InstalledPrograms");
    public static string Nav_JunkCleaner => GetString("Nav_JunkCleaner");
    public static string Nav_EvidenceRemover => GetString("Nav_EvidenceRemover");
    public static string Nav_AutorunManager => GetString("Nav_AutorunManager");
    public static string Nav_DriverStore => GetString("Nav_DriverStore");
    public static string Nav_RepairWindows => GetString("Nav_RepairWindows");
    public static string Nav_History => GetString("Nav_History");
    public static string Nav_About => GetString("Nav_About");
    public static string Status_Ready => GetString("Status_Ready");
    public static string Btn_Uninstall => GetString("Btn_Uninstall");
    public static string Btn_Refresh => GetString("Btn_Refresh");
    public static string Btn_Export => GetString("Btn_Export");
    public static string Btn_CleanSelected => GetString("Btn_CleanSelected");
    public static string Btn_DeleteSelected => GetString("Btn_DeleteSelected");
    public static string Btn_CheckForUpdates => GetString("Btn_CheckForUpdates");
    public static string DryRun_Prefix => GetString("DryRun_Prefix");
    public static string Action_Freed => GetString("Action_Freed");
    public static string Status_ScanComplete => GetString("Status_ScanComplete");
    public static string Confirm_BulkUninstall => GetString("Confirm_BulkUninstall");
}
