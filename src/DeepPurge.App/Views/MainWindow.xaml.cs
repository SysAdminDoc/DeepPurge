using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeepPurge.App.Tray;
using DeepPurge.App.ViewModels;
using DeepPurge.Core.Browsers;
using DeepPurge.Core.Export;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Models;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Schedule;
using DeepPurge.Core.Services;
using DeepPurge.Core.Shell;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Tasks;

namespace DeepPurge.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private DispatcherTimer? _toastTimer;
    private string _currentPanel = "Programs";
    private TrayIconService? _trayIcon;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        try
        {
            foreach (var name in ThemeManager.ThemeNames) cmbTheme.Items.Add(name);
            cmbTheme.SelectedIndex = ThemeManager.CurrentThemeIndex;
        }
        catch
        {
            cmbTheme.Items.Add("Default");
            cmbTheme.SelectedIndex = 0;
        }

        SourceInitialized += (_, _) => _trayIcon = new TrayIconService(this, _vm, RestoreFromTray, ExitFromTray, ShowToast);
        Loaded += OnWindowLoaded;
        StateChanged += OnWindowStateChanged;
    }

    // =============================================================
    //  STARTUP
    // =============================================================

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var scan = _vm.RunInitialScanAsync();
            var completed = await Task.WhenAny(scan, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed != scan)
                ShowToast("Initial scan timed out — showing partial results", isWarning: true);
            else
                await scan;
        }
        catch (OperationCanceledException) { ShowToast("Initial scan timed out — showing partial results", isWarning: true); }
        catch (Exception ex) { ShowToast($"Startup scan error: {ex.Message}", isError: true); }
        finally { FadeOutLoadingOverlay(); }

        var target = (Application.Current as App)?.StartupTargetPath;
        if (!string.IsNullOrEmpty(target))
        {
            if (target.Contains("..")) target = null;
            if (target != null && (File.Exists(target) || Directory.Exists(target)))
            {
                txtForcedName.Text = Path.GetFileNameWithoutExtension(target);
                txtForcedPath.Text = Path.GetDirectoryName(target) ?? "";
                foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;
                panelForced.Visibility = Visibility.Visible;
                _currentPanel = "Forced";
                _vm.CurrentPanel = "Forced";
                ShowToast($"Target: {Path.GetFileName(target)}");
            }
            else
            {
                ShowToast($"Target not found: {target}", isWarning: true);
            }
        }
    }

    private void FadeOutLoadingOverlay()
    {
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) => LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.BeginAnimation(OpacityProperty, anim);
    }

    // =============================================================
    //  THEME
    // =============================================================

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmbTheme.SelectedIndex >= 0) ThemeManager.ApplyTheme(cmbTheme.SelectedIndex);
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.ToggleLightDark();
        cmbTheme.SelectedIndex = ThemeManager.CurrentThemeIndex;
    }

    // =============================================================
    //  TOAST NOTIFICATIONS
    // =============================================================

    private void ShowToast(string message, bool isError = false, bool isWarning = false)
    {
        toastText.Text = message;
        if (isError)       { toastIcon.Text = "\uE711"; toastIcon.Foreground = (Brush)FindResource("RedBrush"); }
        else if (isWarning){ toastIcon.Text = "\uE7BA"; toastIcon.Foreground = (Brush)FindResource("YellowBrush"); }
        else               { toastIcon.Text = "\uE73E"; toastIcon.Foreground = (Brush)FindResource("GreenBrush"); }

        toastPanel.IsHitTestVisible = true;
        toastPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fadeOut.Completed += (_, _) => toastPanel.IsHitTestVisible = false;
            toastPanel.BeginAnimation(OpacityProperty, fadeOut);
        };
        _toastTimer.Start();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        _trayIcon?.ShowBackgroundHint();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    // =============================================================
    //  NAVIGATION
    // =============================================================

    private UIElement[] AllPanels => new UIElement[]
    {
        dgPrograms, panelForced, dgWindowsApps, dgJunk, dgEvidence,
        dgEmptyFolders, panelDisk, dgAutorun, dgBrowserExt, dgContextMenu,
        dgServices, dgTasks, dgRestore, panelLeftovers,
        panelHunter, panelOrphans, panelDeletionRecovery, panelBackups,
        // v0.9.0 system-tools panels
        dgDrivers, dgStartupImpact, dgShortcuts, dgDuplicates,
        panelWinapp2, panelRepair, panelSchedule, panelInstallMonitor, dgHistory, panelSettings, panelAbout,
    };

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || dgPrograms == null) return;
        var tag = rb.Tag as string ?? "";
        _currentPanel = tag;
        _vm.CurrentPanel = tag;

        foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;

        bool isProgramPanel = tag == "Programs";
        toolbarMain.Visibility = isProgramPanel ? Visibility.Visible : Visibility.Collapsed;
        toolbarGeneric.Visibility = isProgramPanel ? Visibility.Collapsed : Visibility.Visible;
        genericButtons.Children.Clear();

        switch (tag)
        {
            case "Programs":
                dgPrograms.Visibility = Visibility.Visible;
                break;
            case "Forced":
                panelForced.Visibility = Visibility.Visible; txtPanelTitle.Text = "Forced Uninstall";
                break;
            case "WindowsApps":
                dgWindowsApps.Visibility = Visibility.Visible; txtPanelTitle.Text = "Windows Apps";
                AppendToolbarButton("Remove Selected", RemoveWindowsApp_Click, "DangerButton");
                break;
            case "Junk":
                dgJunk.Visibility = Visibility.Visible; txtPanelTitle.Text = "Junk Cleaner";
                AppendToolbarButton("Rescan", ScanJunk_Click, "AccentButton");
                AppendToolbarButton("Clean Selected", CleanJunk_Click, "DangerButton");
                break;
            case "Evidence":
                dgEvidence.Visibility = Visibility.Visible; txtPanelTitle.Text = "Evidence Remover";
                AppendToolbarButton("Rescan", ScanEvidence_Click, "AccentButton");
                AppendToolbarButton("Clean Selected", CleanEvidence_Click, "DangerButton");
                break;
            case "EmptyFolders":
                dgEmptyFolders.Visibility = Visibility.Visible; txtPanelTitle.Text = "Empty Folders";
                AppendToolbarButton("Rescan", ScanEmptyFolders_Click, "AccentButton");
                AppendToolbarButton("Delete Selected", DeleteEmptyFolders_Click, "DangerButton");
                break;
            case "Disk":
                panelDisk.Visibility = Visibility.Visible; txtPanelTitle.Text = "Disk Analyzer";
                AppendToolbarButton("Scan System Drive", ScanDisk_Click, "AccentButton");
                AppendToolbarButton("Delete Selected Files", DeleteLargeFiles_Click, "DangerButton");
                break;
            case "Autorun":
                dgAutorun.Visibility = Visibility.Visible; txtPanelTitle.Text = "Autorun Manager";
                AppendToolbarButton("Delete Entry", DeleteAutorun_Click, "DangerButton");
                break;
            case "BrowserExt":
                dgBrowserExt.Visibility = Visibility.Visible; txtPanelTitle.Text = "Browser Extensions";
                AppendToolbarButton("Remove Selected", RemoveBrowserExt_Click, "DangerButton");
                break;
            case "ContextMenu":
                dgContextMenu.Visibility = Visibility.Visible; txtPanelTitle.Text = "Context Menu Cleaner";
                AppendToolbarButton("Rescan", ScanContextMenu_Click, "AccentButton");
                AppendToolbarButton("Remove Orphaned", RemoveContextMenu_Click, "DangerButton");
                break;
            case "Services":
                dgServices.Visibility = Visibility.Visible; txtPanelTitle.Text = "Services Manager";
                AppendToolbarButton("Rescan", ScanServices_Click, "AccentButton");
                AppendToolbarButton("Disable", DisableService_Click);
                AppendToolbarButton("Delete Service", DeleteService_Click, "DangerButton");
                break;
            case "Tasks":
                dgTasks.Visibility = Visibility.Visible; txtPanelTitle.Text = "Scheduled Tasks";
                AppendToolbarButton("Rescan", ScanTasks_Click, "AccentButton");
                AppendToolbarButton("Delete Task", DeleteTask_Click, "DangerButton");
                break;
            case "Restore":
                dgRestore.Visibility = Visibility.Visible; txtPanelTitle.Text = "System Restore Points";
                AppendToolbarButton("Create New", CreateRestorePoint_Click, "AccentButton");
                break;
            case "DeletionRecovery":
                panelDeletionRecovery.Visibility = Visibility.Visible; txtPanelTitle.Text = "Deletion Recovery";
                AppendToolbarButton("Refresh", RefreshDeletionManifests_Click, "AccentButton");
                AppendToolbarButton("Preview", PreviewDeletionManifest_Click);
                AppendToolbarButton("Dry-run Restore", DryRunDeletionManifest_Click, "AccentButton");
                AppendToolbarButton("Restore Selected", RestoreDeletionManifest_Click, "DangerButton");
                AppendToolbarButton("Open Logs", OpenDeletionManifestFolder_Click);
                AppendToolbarButton("Open Backups", OpenDeletionBackupFolder_Click);
                MaybeAutoLoad("DeletionRecovery", () => _vm.LoadDeletionManifestsCommand.Execute(null));
                break;
            case "Hunter":
                panelHunter.Visibility = Visibility.Visible; txtPanelTitle.Text = "Registry Hunter";
                break;
            case "Orphans":
                panelOrphans.Visibility = Visibility.Visible; txtPanelTitle.Text = "Orphaned Artifacts";
                MaybeAutoLoad("Orphans", () => _vm.ScanOrphansCommand.Execute(null));
                break;
            case "Backups":
                panelBackups.Visibility = Visibility.Visible; txtPanelTitle.Text = "Registry Backups";
                AppendToolbarButton("Open Folder", OpenBackupFolder_Click, "AccentButton");
                break;

            // ─── v0.9.0 SYSTEM TOOLS ───
            case "Drivers":
                dgDrivers.Visibility = Visibility.Visible; txtPanelTitle.Text = "Driver Store";
                AppendToolbarButton("Rescan",         ScanDrivers_Click,  "AccentButton");
                AppendToolbarButton("Delete Selected", DeleteDrivers_Click, "DangerButton");
                MaybeAutoLoad("Drivers", () => _vm.ScanDriversCommand.Execute(null));
                break;
            case "StartupImpact":
                dgStartupImpact.Visibility = Visibility.Visible; txtPanelTitle.Text = "Startup Impact";
                AppendToolbarButton("Rescan", ScanStartupImpact_Click, "AccentButton");
                MaybeAutoLoad("StartupImpact", () => _vm.ScanStartupImpactCommand.Execute(null));
                break;
            case "Shortcuts":
                dgShortcuts.Visibility = Visibility.Visible; txtPanelTitle.Text = "Broken Shortcuts";
                AppendToolbarButton("Rescan", ScanShortcuts_Click, "AccentButton");
                AppendToolbarButton("Recycle All", RecycleBrokenShortcuts_Click, "DangerButton");
                MaybeAutoLoad("Shortcuts", () => _vm.ScanShortcutsCommand.Execute(null));
                break;
            case "Duplicates":
                dgDuplicates.Visibility = Visibility.Visible; txtPanelTitle.Text = "Duplicate Files";
                AppendToolbarButton("Scan User Profile", ScanDuplicates_Click, "AccentButton");
                AppendToolbarButton("Delete Duplicates", DeleteDuplicates_Click, "DangerButton");
                // No auto-scan on Duplicates — a user-profile walk is heavy and
                // the user should opt in explicitly.
                break;
            case "Winapp2":
                panelWinapp2.Visibility = Visibility.Visible; txtPanelTitle.Text = "Community Cleaners (winapp2)";
                AppendToolbarButton("Load / Refresh", LoadWinapp2_Click, "AccentButton");
                AppendToolbarButton("Run Applicable", RunWinapp2_Click, "DangerButton");
                MaybeAutoLoad("Winapp2", () => _vm.LoadWinapp2Command.Execute(null));
                break;
            case "Repair":
                panelRepair.Visibility = Visibility.Visible; txtPanelTitle.Text = "Repair Windows";
                break;
            case "Schedule":
                panelSchedule.Visibility = Visibility.Visible; txtPanelTitle.Text = "Scheduled Cleaning";
                AppendToolbarButton("Create Job", CreateScheduledJob_Click, "AccentButton");
                AppendToolbarButton("Remove Job", RemoveScheduledJob_Click, "DangerButton");
                AppendToolbarButton("Refresh", RefreshSchedule_Click, "AccentButton");
                // Refresh every time — schedule list is cheap and can change externally.
                _vm.RefreshScheduledJobsCommand.Execute(null);
                break;
            case "InstallMonitor":
                panelInstallMonitor.Visibility = Visibility.Visible; txtPanelTitle.Text = "Install Monitor";
                break;
            case "History":
                dgHistory.Visibility = Visibility.Visible; txtPanelTitle.Text = "Activity History";
                _vm.LoadHistoryCommand.Execute(null);
                break;
            case "Settings":
                panelSettings.Visibility = Visibility.Visible; txtPanelTitle.Text = "Settings / Privacy";
                AppendToolbarButton("Save Settings", SaveSettings_Click, "AccentButton");
                AppendToolbarButton("Reload", ReloadSettings_Click);
                AppendToolbarButton("Import", ImportSettings_Click);
                AppendToolbarButton("Export", ExportSettings_Click);
                break;
            case "About":
                panelAbout.Visibility = Visibility.Visible; txtPanelTitle.Text = "About / Updates";
                _vm.RefreshAboutTrustFacts();
                AppendToolbarButton("Refresh Trust", RefreshAboutTrust_Click, "AccentButton");
                AppendToolbarButton("Copy SHA256", CopyAboutSha256_Click);
                break;
        }
    }

    // Tracks "this panel has been scanned at least once this session." The old
    // `if (Count == 0) Scan()` guard would re-trigger scans every time the user
    // navigated away and back if the previous scan returned zero results —
    // expensive for Drivers / Shortcuts scans.
    private readonly HashSet<string> _autoLoaded = new(StringComparer.OrdinalIgnoreCase);
    private void MaybeAutoLoad(string panel, Action load)
    {
        if (_autoLoaded.Add(panel)) load();
    }

    // ─── v0.9.0 panel click handlers — all delegate to VM commands so
    //     business logic stays out of the view. Keeps the Ctrl+F
    //     "where does X live?" search consistent with existing handlers.

    private void ScanDrivers_Click(object s, RoutedEventArgs e)
        => _vm.ScanDriversCommand.Execute(null);

    private async void DeleteDrivers_Click(object s, RoutedEventArgs e)
    {
        var row = dgDrivers.SelectedItem as DeepPurge.Core.Drivers.DriverPackage;
        if (row == null) { _vm.StatusText = "Select a driver row first"; return; }
        if (MessageBox.Show(this,
                $"Remove driver package '{row.PublishedName}' ({row.OriginalName})?\n\nThis calls pnputil /delete-driver.",
                "Confirm driver removal", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            var (ok, output) = await new DeepPurge.Core.Drivers.DriverStoreScanner().DeleteAsync(row.PublishedName, force: false);
            _vm.StatusText = ok ? $"Removed {row.PublishedName}" : $"pnputil: {output.Trim()}";
            if (ok) _vm.ScanDriversCommand.Execute(null);
        }
        catch (Exception ex) { _vm.StatusText = $"Driver delete failed: {ex.Message}"; }
    }

    private void ScanStartupImpact_Click(object s, RoutedEventArgs e)
        => _vm.ScanStartupImpactCommand.Execute(null);

    private void ScanShortcuts_Click(object s, RoutedEventArgs e)
        => _vm.ScanShortcutsCommand.Execute(null);

    private void RecycleBrokenShortcuts_Click(object s, RoutedEventArgs e)
        => _vm.RecycleBrokenShortcutsCommand.Execute(null);

    private void ScanDuplicates_Click(object s, RoutedEventArgs e)
        => _vm.ScanDuplicatesCommand.Execute(null);

    private void DeleteDuplicates_Click(object s, RoutedEventArgs e)
    {
        if (_vm.DuplicateGroups.Count == 0) { _vm.StatusText = "Run a scan first."; return; }
        if (MessageBox.Show(this,
                $"Delete the oldest copy from each of {_vm.DuplicateGroups.Count} duplicate group(s)?",
                "Confirm duplicate cleanup", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _vm.DeleteDuplicatesCommand.Execute(null);
    }

    private void LoadWinapp2_Click(object s, RoutedEventArgs e)
        => _vm.LoadWinapp2Command.Execute(null);

    private void RunWinapp2_Click(object s, RoutedEventArgs e)
    {
        if (_vm.Winapp2Entries.Count == 0) { _vm.StatusText = "Load cleaners first."; return; }
        if (MessageBox.Show(this,
                $"Run {_vm.Winapp2Entries.Count} applicable cleaners?\n\n" +
                (_vm.DryRunEnabled ? "(dry-run — no files will be deleted)" : "Files will be deleted."),
                "Confirm winapp2 run", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _vm.RunWinapp2Command.Execute(null);
    }

    private void CreateScheduledJob_Click(object s, RoutedEventArgs e)
    {
        var name = txtScheduleName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _vm.StatusText = "Enter a scheduled job name.";
            ShowToast(_vm.StatusText, isWarning: true);
            return;
        }

        if (!TryParseScheduleTime(txtScheduleTime.Text, out var hour, out var minute))
        {
            _vm.StatusText = "Enter schedule time as HH:MM.";
            ShowToast(_vm.StatusText, isWarning: true);
            return;
        }

        var frequency = ParseScheduleFrequency(GetComboTag(cmbScheduleFrequency, "Weekly"));
        var day = ParseDayOfWeek(GetComboTag(cmbScheduleDay, "Monday"));
        var args = GetComboTag(cmbSchedulePreset, "clean junk evidence");

        var ok = _vm.CreateScheduledJob(name, frequency, day, hour, minute, args);
        ShowToast(ok ? $"Scheduled {name}" : _vm.StatusText, isError: !ok);
    }

    private void RemoveScheduledJob_Click(object s, RoutedEventArgs e)
    {
        if (lstScheduledJobs.SelectedItem is not string name || string.IsNullOrWhiteSpace(name))
        {
            _vm.StatusText = "Select a scheduled job first.";
            ShowToast(_vm.StatusText, isWarning: true);
            return;
        }

        var ok = _vm.DeleteScheduledJob(name);
        ShowToast(ok ? $"Removed {name}" : _vm.StatusText, isError: !ok);
    }

    private void RefreshSchedule_Click(object s, RoutedEventArgs e)
        => _vm.RefreshScheduledJobsCommand.Execute(null);

    private static bool TryParseScheduleTime(string value, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        var parts = (value ?? "").Trim().Split(':', StringSplitOptions.TrimEntries);
        return parts.Length is 1 or 2 &&
               int.TryParse(parts[0], out hour) &&
               int.TryParse(parts.Length == 2 ? parts[1] : "0", out minute) &&
               hour is >= 0 and <= 23 &&
               minute is >= 0 and <= 59;
    }

    private static string GetComboTag(ComboBox combo, string fallback)
        => combo.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;

    private static ScheduleFrequency ParseScheduleFrequency(string value)
        => Enum.TryParse<ScheduleFrequency>(value, ignoreCase: true, out var frequency)
            ? frequency
            : ScheduleFrequency.Weekly;

    private static DayOfWeek ParseDayOfWeek(string value)
        => Enum.TryParse<DayOfWeek>(value, ignoreCase: true, out var day)
            ? day
            : DayOfWeek.Monday;

    private void SaveSettings_Click(object s, RoutedEventArgs e)
    {
        var ok = _vm.SaveSettingsEditor();
        ShowToast(ok ? "Settings saved" : _vm.StatusText, isWarning: !ok);
    }

    private void ReloadSettings_Click(object s, RoutedEventArgs e)
    {
        _vm.ReloadSettingsEditor();
        ShowToast("Settings reloaded");
    }

    private void ExportSettings_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export DeepPurge Settings",
            Filter = "JSON Settings (*.json)|*.json",
            FileName = $"DeepPurge_Settings_{DateTime.Now:yyyyMMdd}.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        var ok = _vm.ExportSettingsTo(dlg.FileName);
        ShowToast(ok ? "Settings exported" : _vm.StatusText, isError: !ok);
    }

    private void ImportSettings_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import DeepPurge Settings",
            Filter = "JSON Settings (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var ok = _vm.ImportSettingsFrom(dlg.FileName);
        ShowToast(ok ? "Settings imported" : _vm.StatusText, isError: !ok);
    }

    private void RefreshAboutTrust_Click(object s, RoutedEventArgs e)
    {
        _vm.RefreshAboutTrustFacts();
        ShowToast("Executable trust facts refreshed");
    }

    private void CopyAboutSha256_Click(object s, RoutedEventArgs e)
    {
        var value = _vm.LocalSha256Display;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("not calculated", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("Refresh trust facts before copying SHA256", isWarning: true);
            return;
        }

        try
        {
            Clipboard.SetText(value);
            ShowToast("Executable SHA256 copied");
        }
        catch (Exception ex)
        {
            ShowToast($"Copy failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Auto-scroll the Repair output box to the tail as new lines stream in.
    /// Wired via TextChanged so it's independent of which repair button
    /// produced the output.
    /// </summary>
    private void RepairOutput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb) tb.ScrollToEnd();
    }

    /// <summary>
    /// Add a button to the generic toolbar in the order you call this method.
    /// (The old code inserted at index 0, which reversed the intended order.)
    /// </summary>
    private void AppendToolbarButton(string text, RoutedEventHandler handler, string? style = null)
    {
        var btn = new Button
        {
            Content = text,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 6, 14, 6),
            MinHeight = 34,
            ToolTip = BuildToolbarHelpText(text),
        };
        AutomationProperties.SetName(btn, text);
        AutomationProperties.SetHelpText(btn, BuildToolbarHelpText(text));
        if (style != null) btn.Style = (Style)FindResource(style);
        btn.Click += handler;
        genericButtons.Children.Add(btn);
    }

    private static string BuildToolbarHelpText(string text)
    {
        return text switch
        {
            "Scan System Drive" => $"Scan the Windows system drive ({GetSystemDriveLabel()}) for folder sizes and large files.",
            "Dry-run Restore" => "Preview which registry and file entries can be restored before applying changes.",
            "Restore Selected" => "Restore the selected deletion manifest where rollback data is available.",
            "Run Applicable" => "Run the currently applicable community cleaner rules with the active safety mode.",
            "Delete Duplicates" => "Delete duplicate copies after the duplicate scan groups matching file hashes.",
            "Delete Selected Files" => "Delete the selected large files using the active dry-run and secure-delete settings.",
            "Recycle All" => "Move all detected broken shortcuts to the Recycle Bin.",
            "Create Job" => "Create a scheduled DeepPurge cleanup job from the selected preset.",
            "Remove Job" => "Remove the selected scheduled DeepPurge cleanup job.",
            "Open Logs" => "Open the folder containing deletion manifest logs.",
            "Open Backups" => "Open the folder containing registry backup exports.",
            "Save Settings" => "Save Settings and Privacy changes to settings.json.",
            "Reload" => "Reload Settings and Privacy values from the saved settings file.",
            "Import" => "Import Settings and Privacy values from a JSON settings file.",
            "Export" => "Export Settings and Privacy values to a JSON settings file.",
            "Refresh Trust" => "Recalculate executable path, signature status, and SHA256 for this running app.",
            "Copy SHA256" => "Copy the running executable SHA256 for release checksum comparison.",
            _ => text,
        };
    }

    private static string GetSystemDriveLabel()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        return string.IsNullOrWhiteSpace(root) ? "system drive" : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // =============================================================
    //  PROGRAMS — UNINSTALL / SCAN / LEFTOVERS
    // =============================================================

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var program = GetSelectedProgram();
        if (program == null) { _vm.StatusText = "Select a program to uninstall"; return; }

        try
        {
            var result = await _vm.UninstallAsync(program, GetScanMode());
            var ok = result?.Success == true;

            // Drop the row immediately on success so the user sees the
            // uninstall take effect without waiting for a full re-scan.
            if (ok) _vm.RemoveProgramFromList(program);

            if (result?.LeftoverScan != null)
            {
                _vm.ShowLeftoverResults(result.LeftoverScan);
                foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;
                panelLeftovers.Visibility = Visibility.Visible;
                btnDeleteLeftovers.IsEnabled = true;
            }

            ShowToast(
                ok ? $"Uninstalled {program.DisplayName}" : $"Uninstall may have issues for {program.DisplayName}",
                isWarning: !ok);
        }
        catch (Exception ex) { ShowToast($"Error: {ex.Message}", isError: true); }
    }

    private async void ScanLeftovers_Click(object sender, RoutedEventArgs e)
    {
        var program = GetSelectedProgram();
        if (program == null) { _vm.StatusText = "Select a program to scan"; return; }

        try
        {
            var result = await _vm.ScanLeftoversAsync(program, GetScanMode());
            _vm.ShowLeftoverResults(result);
            foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;
            panelLeftovers.Visibility = Visibility.Visible;
            btnDeleteLeftovers.IsEnabled = true;

            ShowToast($"Found {_vm.RegistryLeftovers.Count + _vm.FileLeftovers.Count} leftovers");
        }
        catch (Exception ex) { ShowToast($"Scan error: {ex.Message}", isError: true); }
    }

    private async void ForcedScan_Click(object sender, RoutedEventArgs e)
    {
        var name = txtForcedName.Text.Trim();
        if (string.IsNullOrEmpty(name)) { _vm.StatusText = "Enter a program name"; return; }

        try
        {
            var folder = string.IsNullOrWhiteSpace(txtForcedPath.Text) ? null : txtForcedPath.Text.Trim();
            var scanResult = await _vm.ForcedScanAsync(name, folder, GetScanMode());
            _vm.ShowLeftoverResults(scanResult);
            foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;
            panelLeftovers.Visibility = Visibility.Visible;
            btnDeleteLeftovers.IsEnabled = true;

            ShowToast($"Found {scanResult.RegistryLeftovers.Count + scanResult.FileLeftovers.Count} leftovers");
        }
        catch (Exception ex) { ShowToast($"Error: {ex.Message}", isError: true); }
    }

    private async void DeleteLeftovers_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentScanResult == null) { _vm.StatusText = "Nothing to delete"; return; }

        var regItems = _vm.RegistryLeftovers.Where(i => i.IsSelected).ToList();
        var fileItems = _vm.FileLeftovers.Where(i => i.IsSelected).ToList();
        if (regItems.Count == 0 && fileItems.Count == 0)
        {
            _vm.StatusText = "Nothing selected";
            return;
        }

        int blocked = 0;
        foreach (var item in regItems.ToList())
        {
            if (!SafetyGuard.IsRegistryPathSafeToDelete(item.Path))
            { item.IsSelected = false; regItems.Remove(item); blocked++; }
        }
        foreach (var item in fileItems.ToList())
        {
            if (!SafetyGuard.IsPathSafeToDelete(item.Path))
            { item.IsSelected = false; fileItems.Remove(item); blocked++; }
        }
        if (blocked > 0) ShowToast($"{blocked} protected items skipped", isWarning: true);

        try
        {
            var (reg, file) = await _vm.DeleteLeftoversAsync();
            ShowToast($"Deleted {reg} registry + {file} file leftovers");
            dgRegistryLeftovers.Items.Refresh();
            dgFileLeftovers.Items.Refresh();
        }
        catch (Exception ex) { ShowToast($"Delete error: {ex.Message}", isError: true); }
    }

    private void BackToPrograms_Click(object sender, RoutedEventArgs e)
    {
        panelLeftovers.Visibility = Visibility.Collapsed;
        dgPrograms.Visibility = Visibility.Visible;
    }

    private void SelectAllSafe_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectAllSafe();
        dgRegistryLeftovers.Items.Refresh();
        dgFileLeftovers.Items.Refresh();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        _vm.DeselectAll();
        dgRegistryLeftovers.Items.Refresh();
        dgFileLeftovers.Items.Refresh();
    }

    // =============================================================
    //  PANEL ACTIONS (SafetyGuard protected)
    // =============================================================

    private async void ScanJunk_Click(object sender, RoutedEventArgs e) => await _vm.ScanJunkAsync();

    private async void CleanJunk_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.JunkCategories.Where(c => c.IsSelected && c.Files.Count > 0).ToList();
        if (selected.Count == 0) { _vm.StatusText = "Nothing selected to clean"; return; }

        try { await _vm.CleanJunkAsync(selected); }
        catch (Exception ex) { ShowToast($"Clean error: {ex.Message}", isError: true); }
    }

    private async void ScanEvidence_Click(object sender, RoutedEventArgs e) => await _vm.ScanEvidenceAsync();

    private async void CleanEvidence_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.TraceCategories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) { _vm.StatusText = "Nothing selected"; return; }
        try
        {
            await _vm.CleanEvidenceAsync(selected);
            ShowToast("Traces cleaned");
        }
        catch (Exception ex) { ShowToast($"Clean error: {ex.Message}", isError: true); }
    }

    private async void ScanEmptyFolders_Click(object sender, RoutedEventArgs e) => await _vm.ScanEmptyFoldersAsync();

    private async void DeleteEmptyFolders_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.EmptyFolders
            .Where(f => f.IsSelected && SafetyGuard.IsPathSafeToDelete(f.Path))
            .ToList();
        if (selected.Count == 0) { _vm.StatusText = "Nothing selected"; return; }

        await _vm.DeleteEmptyFoldersAsync(selected);
        ShowToast($"Deleted {selected.Count} empty folders");
    }

    private async void ScanDisk_Click(object sender, RoutedEventArgs e) => await _vm.ScanDiskAsync();

    private async void DeleteLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.LargeFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) { _vm.StatusText = "Nothing selected"; return; }
        _vm.IsBusy = true;
        _vm.StatusText = $"Deleting {selected.Count} files...";
        int deleted = 0, skipped = 0;
        await Task.Run(() =>
        {
            foreach (var f in selected)
            {
                if (!SafetyGuard.IsPathSafeToDelete(f.Path)) { Interlocked.Increment(ref skipped); continue; }
                try { if (SafetyGuard.SafeDeleteFile(f.Path)) Interlocked.Increment(ref deleted); else Interlocked.Increment(ref skipped); }
                catch { Interlocked.Increment(ref skipped); }
            }
        });
        var deletedItems = selected.Where(f => !File.Exists(f.Path)).ToList();
        foreach (var f in deletedItems) _vm.LargeFiles.Remove(f);
        _vm.IsBusy = false;
        ShowToast($"Deleted {deleted} files" + (skipped > 0 ? $" ({skipped} skipped)" : ""));
    }

    private async void ScanContextMenu_Click(object sender, RoutedEventArgs e) => await _vm.ScanContextMenuAsync();

    private void RemoveContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.ContextMenuEntries.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) { _vm.StatusText = "Nothing selected"; return; }
        var removed = ContextMenuCleaner.RemoveOrphanedEntries(selected);
        foreach (var item in selected) _vm.ContextMenuEntries.Remove(item);
        ShowToast($"Removed {removed} context menu entries");
    }

    private async void ScanServices_Click(object sender, RoutedEventArgs e) => await _vm.ScanServicesAsync();

    private void DisableService_Click(object sender, RoutedEventArgs e)
    {
        if (dgServices.SelectedItem is not ServiceEntry svc)
        { _vm.StatusText = "Select a service"; return; }
        if (!SafetyGuard.IsServiceSafeToModify(svc.Name))
        { ShowToast($"{svc.DisplayName} is a protected Windows service", isWarning: true); return; }

        if (ServiceScanner.DisableService(svc))
        { svc.StartType = "Disabled"; dgServices.Items.Refresh(); ShowToast($"Disabled {svc.DisplayName}"); }
        else ShowToast($"Failed to disable {svc.DisplayName}", isError: true);
    }

    private void DeleteService_Click(object sender, RoutedEventArgs e)
    {
        if (dgServices.SelectedItem is not ServiceEntry svc)
        { _vm.StatusText = "Select a service"; return; }
        if (!SafetyGuard.IsServiceSafeToModify(svc.Name))
        { ShowToast($"{svc.DisplayName} is a protected Windows service", isWarning: true); return; }

        if (ServiceScanner.DeleteService(svc))
        { _vm.Services.Remove(svc); ShowToast($"Deleted {svc.DisplayName}"); }
        else ShowToast($"Failed to delete {svc.DisplayName}", isError: true);
    }

    private async void ScanTasks_Click(object sender, RoutedEventArgs e) => await _vm.ScanTasksAsync();

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (dgTasks.SelectedItem is not ScheduledTaskInfo task)
        { _vm.StatusText = "Select a task"; return; }
        if (!SafetyGuard.IsTaskSafeToDelete(task.Path ?? task.Name))
        { ShowToast($"{task.Name} is a protected Windows task", isWarning: true); return; }

        if (ScheduledTaskScanner.DeleteTask(task))
        { _vm.ScheduledTasks.Remove(task); ShowToast($"Deleted {task.Name}"); }
        else ShowToast($"Failed to delete {task.Name}", isError: true);
    }

    private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsBusy = true; _vm.StatusText = "Creating system restore point...";
        try
        {
            var ok = await Task.Run(() => RestorePointManager.CreateRestorePoint("DeepPurge Manual Checkpoint"));
            if (ok)
            {
                ShowToast("Restore point created");
                var pts = await Task.Run(() => DeepPurge.Core.Safety.SystemRestoreManager.GetRestorePoints());
                _vm.RestorePoints.Clear();
                foreach (var p in pts) _vm.RestorePoints.Add(p);
            }
            else ShowToast("Failed to create restore point", isError: true);
        }
        finally { _vm.IsBusy = false; }
    }

    private void DeleteAutorun_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is not AutorunEntry entry)
        { _vm.StatusText = "Select an autorun entry"; return; }
        if (!SafetyGuard.IsAutorunSafeToDelete(entry.Command))
        { ShowToast($"{entry.Name} is a protected system entry", isWarning: true); return; }

        if (AutorunScanner.DeleteAutorun(entry))
        { _vm.Autoruns.Remove(entry); ShowToast($"Deleted {entry.Name}"); }
        else ShowToast($"Failed to delete {entry.Name}", isError: true);
    }

    private async void RemoveWindowsApp_Click(object sender, RoutedEventArgs e)
    {
        if (dgWindowsApps.SelectedItem is not WindowsApp app)
        { _vm.StatusText = "Select an app"; return; }
        if (app.IsNonRemovable)
        { ShowToast($"{app.DisplayName} is non-removable", isWarning: true); return; }

        _vm.IsBusy = true;
        try
        {
            var ok = await WindowsAppManager.RemoveAppAsync(app);
            if (ok) { _vm.WindowsApps.Remove(app); ShowToast($"Removed {app.DisplayName}"); }
            else ShowToast($"Failed to remove {app.DisplayName}", isError: true);
        }
        finally { _vm.IsBusy = false; }
    }

    private void RemoveBrowserExt_Click(object sender, RoutedEventArgs e)
    {
        if (dgBrowserExt.SelectedItem is not BrowserExtension ext)
        { _vm.StatusText = "Select an extension"; return; }

        var ok = BrowserExtensionScanner.RemoveExtension(ext);
        if (ok) { _vm.BrowserExtensions.Remove(ext); ShowToast($"Removed {ext.Name}. Restart {ext.Browser}."); }
        else ShowToast($"Failed. Is {ext.Browser} still running?", isError: true);
    }

    // =============================================================
    //  CONTEXT MENU — CHECK ALL / UNCHECK ALL / INVERT
    // =============================================================

    private void ToggleAllItems(DataGrid? grid, bool value)
    {
        if (grid?.ItemsSource is not System.Collections.IEnumerable items) return;
        foreach (var item in items)
        {
            var prop = item.GetType().GetProperty("IsSelected");
            if (prop != null && prop.CanWrite) prop.SetValue(item, value);
        }
        grid.Items.Refresh();
        _vm.UpdateSelectedCount();
    }

    private void Ctx_CheckAll_Click(object sender, RoutedEventArgs e)
    { ToggleAllItems(FindParentGrid(sender), true); ShowToast("All items checked"); }

    private void Ctx_UncheckAll_Click(object sender, RoutedEventArgs e)
    { ToggleAllItems(FindParentGrid(sender), false); ShowToast("All items unchecked"); }

    private void Ctx_InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        var grid = FindParentGrid(sender);
        if (grid?.ItemsSource is not System.Collections.IEnumerable items) return;
        foreach (var item in items)
        {
            var prop = item.GetType().GetProperty("IsSelected");
            if (prop != null && prop.CanWrite)
                prop.SetValue(item, !(bool)(prop.GetValue(item) ?? false));
        }
        grid.Items.Refresh();
        _vm.UpdateSelectedCount();
    }

    // =============================================================
    //  CONTEXT MENU — FAST ACTIONS
    // =============================================================

    private void Ctx_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (dgPrograms.SelectedItem is InstalledProgram p && !string.IsNullOrEmpty(p.InstallLocation))
            TryStart("explorer.exe", p.InstallLocation);
        else _vm.StatusText = "No install location available";
    }

    private void Ctx_OpenRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (dgPrograms.SelectedItem is not InstalledProgram p || string.IsNullOrEmpty(p.RegistryPath)) return;

        try
        {
            var fullPath = p.RegistryPath
                .Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                .Replace("HKCU\\", "HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)
                .Replace("HKCR\\", "HKEY_CLASSES_ROOT\\", StringComparison.OrdinalIgnoreCase)
                .Replace("HKU\\",  "HKEY_USERS\\",        StringComparison.OrdinalIgnoreCase);

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", writable: true);
            key?.SetValue("LastKey", fullPath);
            TryStart("regedit.exe");
        }
        catch { _vm.StatusText = "Could not open Registry Editor"; }
    }

    private void Ctx_CopyName_Click(object sender, RoutedEventArgs e)
    {
        var grid = FindParentGrid(sender);
        if (grid?.SelectedItem == null) return;
        var item = grid.SelectedItem;
        string? text = null;
        foreach (var propName in new[] { "DisplayName", "Name", "Description" })
        {
            var prop = item.GetType().GetProperty(propName);
            if (prop != null) { text = prop.GetValue(item)?.ToString(); if (!string.IsNullOrEmpty(text)) break; }
        }
        if (!string.IsNullOrEmpty(text)) { Clipboard.SetText(text); ShowToast($"Copied: {text}"); }
    }

    private void Ctx_CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is AutorunEntry entry && !string.IsNullOrEmpty(entry.Command))
        { Clipboard.SetText(entry.Command); ShowToast("Command copied"); }
    }

    private void Ctx_OpenAutorunPath_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is not AutorunEntry entry || string.IsNullOrEmpty(entry.Command)) return;
        var path = entry.Command.Trim('"').Split(' ')[0];
        if (File.Exists(path)) TryStart("explorer.exe", $"/select,\"{path}\"");
    }

    private void Ctx_ToggleAutorun_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is not AutorunEntry entry) return;
        if (AutorunScanner.ToggleAutorun(entry))
        {
            dgAutorun.Items.Refresh();
            ShowToast($"{(entry.IsEnabled ? "Enabled" : "Disabled")} {entry.Name}");
        }
        else ShowToast($"Failed to toggle {entry.Name}", isError: true);
    }

    private void Ctx_ExcludePath_Click(object sender, RoutedEventArgs e)
    {
        var grid = FindParentGrid(sender);
        if (grid?.SelectedItem == null) { _vm.StatusText = "Nothing selected"; return; }
        var item = grid.SelectedItem;
        string? path = null;
        foreach (var propName in new[] { "Path", "DisplayPath", "Directory" })
        {
            var prop = item.GetType().GetProperty(propName);
            if (prop != null) { path = prop.GetValue(item)?.ToString(); if (!string.IsNullOrEmpty(path)) break; }
        }
        if (string.IsNullOrEmpty(path)) { _vm.StatusText = "No path available for exclusion"; return; }
        var settings = DeepPurge.Core.App.AppSettings.Current;
        if (!settings.ExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            settings.ExcludedPaths.Add(path);
            settings.Save();
            ShowToast($"Excluded: {path}");
        }
        else ShowToast($"Already excluded: {path}");
    }

    private void Ctx_OpenFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (dgEmptyFolders.SelectedItem is not EmptyFolderInfo folder) return;
        var parent = Path.GetDirectoryName(folder.Path);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            TryStart("explorer.exe", parent);
    }

    private void Ctx_OpenExtFolder_Click(object sender, RoutedEventArgs e)
    {
        if (dgBrowserExt.SelectedItem is BrowserExtension ext && !string.IsNullOrEmpty(ext.Path))
            TryStart("explorer.exe", ext.Path);
    }

    private void Ctx_OpenServicesMsc_Click(object sender, RoutedEventArgs e) => TryStart("services.msc");
    private void Ctx_OpenTaskScheduler_Click(object sender, RoutedEventArgs e) => TryStart("taskschd.msc");

    // =============================================================
    //  MASTER CHECKBOX
    // =============================================================

    private void MasterCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        ToggleAllItems(FindParentGrid(sender), cb.IsChecked == true);
    }

    // =============================================================
    //  EXPORT
    // =============================================================

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Installed Programs",
            Filter = "HTML Report (*.html)|*.html|CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = $"DeepPurge_Export_{DateTime.Now:yyyyMMdd}",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var programs = _vm.Programs.ToList();
            switch (ext)
            {
                case ".csv":  ProgramExporter.ExportToCsv(programs, dlg.FileName); break;
                case ".json": ProgramExporter.ExportToJson(programs, dlg.FileName); break;
                default:      ProgramExporter.ExportToHtml(programs, dlg.FileName); break;
            }
            ShowToast($"Exported {programs.Count} programs");
            TryStart("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex) { ShowToast($"Export failed: {ex.Message}", isError: true); }
    }

    // =============================================================
    //  HELPERS
    // =============================================================

    private DataGrid? FindParentGrid(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is DataGrid dg) return dg;
        if (sender is DependencyObject dep)
        {
            DependencyObject? parent = dep;
            while (parent != null)
            {
                parent = VisualTreeHelper.GetParent(parent);
                if (parent is DataGrid g) return g;
            }
        }
        return _currentPanel switch
        {
            "Programs"     => dgPrograms,
            "Junk"         => dgJunk,
            "Evidence"     => dgEvidence,
            "EmptyFolders" => dgEmptyFolders,
            "ContextMenu"  => dgContextMenu,
            "Services"     => dgServices,
            "Tasks"        => dgTasks,
            "Restore"      => dgRestore,
            _ => null,
        };
    }

    private InstalledProgram? GetSelectedProgram() =>
        dgPrograms.SelectedItem as InstalledProgram ?? _vm.FilteredPrograms.FirstOrDefault(p => p.IsSelected);

    private DeepPurge.Core.Models.ScanMode GetScanMode()
    {
        if (rbSafe?.IsChecked == true)     return DeepPurge.Core.Models.ScanMode.Safe;
        if (rbAdvanced?.IsChecked == true) return DeepPurge.Core.Models.ScanMode.Advanced;
        return DeepPurge.Core.Models.ScanMode.Moderate;
    }

    private static void TryStart(string fileName, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args ?? "",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch { /* best-effort — don't crash the UI on failed Process.Start */ }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _vm.SearchFilter = txtSearch.Text;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private void Programs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dgPrograms.SelectedItem is InstalledProgram p)
            _vm.StatusText = $"{p.DisplayName}  |  {p.Publisher}  |  {p.DisplayVersion}  |  {p.InstallLocation}";
    }

    private void Programs_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgPrograms.SelectedItem is InstalledProgram p && !string.IsNullOrEmpty(p.InstallLocation))
            TryStart("explorer.exe", p.InstallLocation);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the program's install folder" };
        if (dlg.ShowDialog() == true) txtForcedPath.Text = dlg.FolderName;
    }

    private void BrowseInstaller_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the installer executable",
            Filter = "Executables (*.exe;*.msi)|*.exe;*.msi|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true) txtMonitorInstaller.Text = dlg.FileName;
    }

    private async void StartInstallTrace_Click(object sender, RoutedEventArgs e)
    {
        var name = txtMonitorName.Text.Trim();
        var installer = txtMonitorInstaller.Text.Trim();
        var args = txtMonitorArgs.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installer))
        {
            _vm.StatusText = "Enter a program name and installer path first.";
            return;
        }
        _vm.StatusText = "Tracing installer — do not close DeepPurge...";
        var delta = await _vm.TraceInstallerAsync(name, installer, string.IsNullOrEmpty(args) ? null : args);
        if (delta != null)
        {
            DeepPurge.Core.Diagnostics.ActivityLog.Record("snapshot", $"{name}: +{delta.AddedFiles.Count} files, +{delta.AddedRegistryKeys.Count} keys", delta.TotalAddedBytes, delta.AddedFiles.Count);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _vm.CancelOperation();
        _trayIcon?.Dispose();
        base.OnClosing(e);
    }

    // =============================================================
    //  v0.8 — bulk / hunter / winget / backups
    // =============================================================

    private async void BulkUninstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Programs.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowToast("Tick the checkbox next to programs to uninstall in bulk", isWarning: true);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Uninstall {selected.Count} programs silently?\n\nA single restore point is created at the start. " +
            "Each uninstaller runs with its known silent flag (/S, /qn, /VERYSILENT, etc.).",
            "DeepPurge — Bulk Uninstall",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            var results = await _vm.UninstallSelectedAsync(GetScanMode());
            if (results == null) return;
            var ok = results.Count(r => r.Success);
            ShowToast(
                ok == results.Count
                    ? $"Uninstalled all {ok} programs"
                    : $"Uninstalled {ok}/{results.Count} programs (see status bar for details)",
                isWarning: ok != results.Count);
        }
        catch (Exception ex) { ShowToast($"Bulk uninstall error: {ex.Message}", isError: true); }
    }

    private async void RegistryHunt_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.HuntRegistryAsync(txtHunter.Text.Trim()); }
        catch (Exception ex) { ShowToast($"Hunt error: {ex.Message}", isError: true); }
    }

    private async void HunterInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            try { await _vm.HuntRegistryAsync(txtHunter.Text.Trim()); }
            catch (Exception ex) { ShowToast($"Hunt error: {ex.Message}", isError: true); }
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e) => _vm.OpenBackupFolder();

    private void RefreshDeletionManifests_Click(object sender, RoutedEventArgs e)
        => _vm.LoadDeletionManifestsCommand.Execute(null);

    private void PreviewDeletionManifest_Click(object sender, RoutedEventArgs e)
        => _vm.PreviewSelectedDeletionManifestCommand.Execute(null);

    private void DryRunDeletionManifest_Click(object sender, RoutedEventArgs e)
        => _vm.DryRunRestoreDeletionManifestCommand.Execute(null);

    private void RestoreDeletionManifest_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDeletionManifest == null) { _vm.StatusText = "Select a deletion manifest first."; return; }
        if (MessageBox.Show(this,
                $"Restore registry backups referenced by {_vm.SelectedDeletionManifest.Date:yyyy-MM-dd}?\n\n" +
                "Files are not restored automatically; DeepPurge reports Recycle Bin recovery candidates.",
                "Confirm deletion manifest restore", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _vm.RestoreSelectedDeletionManifestCommand.Execute(null);
    }

    private void OpenDeletionManifestFolder_Click(object sender, RoutedEventArgs e)
        => _vm.OpenDeletionManifestFolderCommand.Execute(null);

    private void OpenDeletionBackupFolder_Click(object sender, RoutedEventArgs e)
        => _vm.OpenDeletionBackupFolderCommand.Execute(null);

    private void OrphanScan_Click(object sender, RoutedEventArgs e)
        => _vm.ScanOrphansCommand.Execute(null);

    private void DeleteOrphanedFirewallRules_Click(object sender, RoutedEventArgs e)
        => _vm.DeleteOrphanedFirewallRulesCommand.Execute(null);

    private void RemoveOrphanedPathEntries_Click(object sender, RoutedEventArgs e)
        => _vm.RemoveOrphanedPathEntriesCommand.Execute(null);

    /// <summary>
    /// Context-menu action: push the program's upgrade through winget.
    /// Only meaningful when <see cref="InstalledProgram.UpgradeAvailable"/> is populated
    /// by <c>PackageManagerScanner</c>; otherwise we offer to run a search anyway.
    /// </summary>
    private void Ctx_WingetUpgrade_Click(object sender, RoutedEventArgs e)
    {
        if (dgPrograms.SelectedItem is not InstalledProgram p) return;

        if (string.IsNullOrEmpty(p.PackageId))
        {
            ShowToast($"{p.DisplayName} isn't tracked by winget", isWarning: true);
            return;
        }

        try
        {
            var psi = PackageManagerCommandBuilder.CreateWingetUpgradeStartInfo(p.PackageId);
            Process.Start(psi);
            ShowToast($"Launching winget upgrade for {p.DisplayName}");
        }
        catch (ArgumentException)
        {
            DeepPurge.Core.Diagnostics.Log.Warn($"Blocked unsafe winget package id: {p.PackageId}");
            ShowToast($"Unsafe winget package id blocked: {p.PackageId}", isWarning: true);
        }
        catch (Exception ex) { ShowToast($"winget launch failed: {ex.Message}", isError: true); }
    }
}
