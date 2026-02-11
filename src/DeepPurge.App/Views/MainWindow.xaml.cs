using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeepPurge.Core.Models;
using DeepPurge.Core.Export;
using DeepPurge.Core.Safety;
using DeepPurge.App.ViewModels;

namespace DeepPurge.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private string _currentPanel = "Programs";

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        foreach (var name in ThemeManager.ThemeNames) cmbTheme.Items.Add(name);
        cmbTheme.SelectedIndex = 0;
        Loaded += OnWindowLoaded;
    }

    // =============================================================
    //  STARTUP
    // =============================================================

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.RunInitialScanAsync();
        FadeOutLoadingOverlay();
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
    { if (cmbTheme.SelectedIndex >= 0) ThemeManager.ApplyTheme(cmbTheme.SelectedIndex); }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    { ThemeManager.ToggleLightDark(); cmbTheme.SelectedIndex = ThemeManager.CurrentThemeIndex; }

    // =============================================================
    //  TOAST NOTIFICATIONS
    // =============================================================

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    private void ShowToast(string message, bool isError = false, bool isWarning = false)
    {
        toastText.Text = message;
        if (isError) { toastIcon.Text = "\uE711"; toastIcon.Foreground = (Brush)FindResource("RedBrush"); }
        else if (isWarning) { toastIcon.Text = "\uE7BA"; toastIcon.Foreground = (Brush)FindResource("YellowBrush"); }
        else { toastIcon.Text = "\uE73E"; toastIcon.Foreground = (Brush)FindResource("GreenBrush"); }

        toastPanel.IsHitTestVisible = true;
        toastPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        _toastTimer?.Stop();
        _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fadeOut.Completed += (_, _) => toastPanel.IsHitTestVisible = false;
            toastPanel.BeginAnimation(OpacityProperty, fadeOut);
        };
        _toastTimer.Start();
    }

    // =============================================================
    //  NAVIGATION
    // =============================================================

    private UIElement[] AllPanels => new UIElement[]
    {
        dgPrograms, panelForced, dgWindowsApps, dgJunk, dgEvidence,
        dgEmptyFolders, panelDisk, dgAutorun, dgBrowserExt, dgContextMenu,
        dgServices, dgTasks, dgRestore, panelLeftovers
    };

    private async void NavButton_Checked(object sender, RoutedEventArgs e)
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
            case "Programs": dgPrograms.Visibility = Visibility.Visible; break;
            case "Forced": panelForced.Visibility = Visibility.Visible; txtPanelTitle.Text = "Forced Uninstall"; break;
            case "WindowsApps":
                dgWindowsApps.Visibility = Visibility.Visible; txtPanelTitle.Text = "Windows Apps";
                AddBtn("Remove Selected", RemoveWindowsApp_Click, "DangerButton"); break;
            case "Junk":
                dgJunk.Visibility = Visibility.Visible; txtPanelTitle.Text = "Junk Cleaner";
                AddBtn("Clean Selected", CleanJunk_Click, "DangerButton");
                AddBtn("Rescan", ScanJunk_Click, "AccentButton"); break;
            case "Evidence":
                dgEvidence.Visibility = Visibility.Visible; txtPanelTitle.Text = "Evidence Remover";
                AddBtn("Clean Selected", CleanEvidence_Click, "DangerButton");
                AddBtn("Rescan", ScanEvidence_Click, "AccentButton"); break;
            case "EmptyFolders":
                dgEmptyFolders.Visibility = Visibility.Visible; txtPanelTitle.Text = "Empty Folders";
                AddBtn("Delete Selected", DeleteEmptyFolders_Click, "DangerButton");
                AddBtn("Rescan", ScanEmptyFolders_Click, "AccentButton"); break;
            case "Disk":
                panelDisk.Visibility = Visibility.Visible; txtPanelTitle.Text = "Disk Analyzer";
                AddBtn("Delete Selected Files", DeleteLargeFiles_Click, "DangerButton");
                AddBtn("Scan C:\\", ScanDisk_Click, "AccentButton"); break;
            case "Autorun":
                dgAutorun.Visibility = Visibility.Visible; txtPanelTitle.Text = "Autorun Manager";
                AddBtn("Delete Entry", DeleteAutorun_Click, "DangerButton"); break;
            case "BrowserExt":
                dgBrowserExt.Visibility = Visibility.Visible; txtPanelTitle.Text = "Browser Extensions";
                AddBtn("Remove Selected", RemoveBrowserExt_Click, "DangerButton"); break;
            case "ContextMenu":
                dgContextMenu.Visibility = Visibility.Visible; txtPanelTitle.Text = "Context Menu Cleaner";
                AddBtn("Remove Orphaned", RemoveContextMenu_Click, "DangerButton");
                AddBtn("Rescan", ScanContextMenu_Click, "AccentButton"); break;
            case "Services":
                dgServices.Visibility = Visibility.Visible; txtPanelTitle.Text = "Services Manager";
                AddBtn("Delete Service", DeleteService_Click, "DangerButton");
                AddBtn("Disable", DisableService_Click);
                AddBtn("Rescan", ScanServices_Click, "AccentButton"); break;
            case "Tasks":
                dgTasks.Visibility = Visibility.Visible; txtPanelTitle.Text = "Scheduled Tasks";
                AddBtn("Delete Task", DeleteTask_Click, "DangerButton");
                AddBtn("Rescan", ScanTasks_Click, "AccentButton"); break;
            case "Restore":
                dgRestore.Visibility = Visibility.Visible; txtPanelTitle.Text = "System Restore Points";
                AddBtn("Create New", CreateRestorePoint_Click, "AccentButton"); break;
        }
    }

    private void AddBtn(string text, RoutedEventHandler handler, string? style = null)
    {
        var btn = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        if (style != null) btn.Style = (Style)FindResource(style);
        btn.Click += handler;
        genericButtons.Children.Insert(0, btn);
    }

    // =============================================================
    //  PROGRAMS — UNINSTALL / SCAN / LEFTOVERS
    // =============================================================

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var program = GetSelectedProgram();
        if (program == null) { _vm.StatusText = "Select a program to uninstall"; return; }
        _vm.IsBusy = true; _vm.StatusText = $"Uninstalling {program.DisplayName}...";
        try
        {
            var engine = new DeepPurge.Core.Uninstall.UninstallEngine();
            engine.StatusChanged += s => Dispatcher.Invoke(() => _vm.StatusText = s);
            var result = await engine.UninstallAsync(program, GetScanMode(), silent: false);
            if (result.LeftoverScan != null)
            {
                _vm.ShowLeftoverResults(result.LeftoverScan);
                foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;
                panelLeftovers.Visibility = Visibility.Visible;
                btnDeleteLeftovers.IsEnabled = true;
            }
            ShowToast(result.Success ? $"Uninstalled {program.DisplayName}" : $"Uninstall may have issues for {program.DisplayName}",
                isWarning: !result.Success);
        }
        catch (Exception ex) { ShowToast($"Error: {ex.Message}", isError: true); }
        finally { _vm.IsBusy = false; }
    }

    private async void ScanLeftovers_Click(object sender, RoutedEventArgs e)
    {
        var program = GetSelectedProgram();
        if (program == null) { _vm.StatusText = "Select a program to scan"; return; }
        _vm.IsBusy = true; _vm.StatusText = $"Scanning leftovers for {program.DisplayName}...";
        try
        {
            var engine = new DeepPurge.Core.Uninstall.UninstallEngine();
            engine.StatusChanged += s => Dispatcher.Invoke(() => _vm.StatusText = s);
            var result = await engine.UninstallAsync(program, GetScanMode(), createRestorePoint: false, runBuiltInUninstaller: false);
            if (result.LeftoverScan != null)
            {
                _vm.ShowLeftoverResults(result.LeftoverScan);
                foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;
                panelLeftovers.Visibility = Visibility.Visible;
                btnDeleteLeftovers.IsEnabled = true;
            }
            ShowToast($"Found {_vm.RegistryLeftovers.Count + _vm.FileLeftovers.Count} leftovers");
        }
        catch (Exception ex) { ShowToast($"Scan error: {ex.Message}", isError: true); }
        finally { _vm.IsBusy = false; }
    }

    private async void ForcedScan_Click(object sender, RoutedEventArgs e)
    {
        var name = txtForcedName.Text.Trim();
        if (string.IsNullOrEmpty(name)) { _vm.StatusText = "Enter a program name"; return; }
        _vm.IsBusy = true;
        try
        {
            var engine = new DeepPurge.Core.Uninstall.UninstallEngine();
            engine.StatusChanged += s => Dispatcher.Invoke(() => _vm.StatusText = s);
            var scanResult = await engine.ForcedScanAsync(name, txtForcedPath.Text.Trim(), GetScanMode());
            _vm.ShowLeftoverResults(scanResult);
            foreach (var p in AllPanels) p.Visibility = Visibility.Collapsed;
            panelLeftovers.Visibility = Visibility.Visible;
            btnDeleteLeftovers.IsEnabled = true;
            ShowToast($"Found {scanResult.RegistryLeftovers.Count + scanResult.FileLeftovers.Count} leftovers");
        }
        catch (Exception ex) { ShowToast($"Error: {ex.Message}", isError: true); }
        finally { _vm.IsBusy = false; }
    }

    private async void DeleteLeftovers_Click(object sender, RoutedEventArgs e)
    {
        var regItems = _vm.RegistryLeftovers.Where(i => i.IsSelected).ToList();
        var fileItems = _vm.FileLeftovers.Where(i => i.IsSelected).ToList();
        if (!regItems.Any() && !fileItems.Any()) { _vm.StatusText = "Nothing selected"; return; }

        // SafetyGuard validation — silently deselect protected items
        int blocked = 0;
        foreach (var item in regItems.ToList())
        { if (!SafetyGuard.IsRegistryPathSafeToDelete(item.Path)) { item.IsSelected = false; regItems.Remove(item); blocked++; } }
        foreach (var item in fileItems.ToList())
        { if (!SafetyGuard.IsPathSafeToDelete(item.Path)) { item.IsSelected = false; fileItems.Remove(item); blocked++; } }

        if (blocked > 0) ShowToast($"{blocked} protected items skipped", isWarning: true);

        _vm.IsBusy = true;
        try
        {
            var engine = new DeepPurge.Core.Uninstall.UninstallEngine();
            engine.StatusChanged += s => Dispatcher.Invoke(() => _vm.StatusText = s);
            var (reg, file) = await engine.DeleteLeftoversAsync(regItems, fileItems);
            ShowToast($"Deleted {reg} registry + {file} file leftovers");
        }
        catch (Exception ex) { ShowToast($"Delete error: {ex.Message}", isError: true); }
        finally { _vm.IsBusy = false; }
    }

    private void BackToPrograms_Click(object sender, RoutedEventArgs e)
    { panelLeftovers.Visibility = Visibility.Collapsed; dgPrograms.Visibility = Visibility.Visible; }

    private void SelectAllSafe_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _vm.RegistryLeftovers) item.IsSelected = item.Confidence == LeftoverConfidence.Safe;
        foreach (var item in _vm.FileLeftovers) item.IsSelected = item.Confidence == LeftoverConfidence.Safe;
        dgRegistryLeftovers.Items.Refresh(); dgFileLeftovers.Items.Refresh();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _vm.RegistryLeftovers) item.IsSelected = false;
        foreach (var item in _vm.FileLeftovers) item.IsSelected = false;
        dgRegistryLeftovers.Items.Refresh(); dgFileLeftovers.Items.Refresh();
    }

    // =============================================================
    //  PANEL ACTIONS (SafetyGuard protected)
    // =============================================================

    private async void ScanJunk_Click(object sender, RoutedEventArgs e) => await _vm.ScanJunkAsync();
    private async void CleanJunk_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.JunkCategories.Where(c => c.IsSelected && c.Files.Count > 0).ToList();
        if (!selected.Any()) { _vm.StatusText = "Nothing selected to clean"; return; }
        _vm.IsBusy = true;
        int cleaned = 0, skipped = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var cat in selected)
                    foreach (var file in cat.Files)
                    {
                        if (!SafetyGuard.IsJunkPathSafeToDelete(file.Path)) { skipped++; continue; }
                        try
                        {
                            if (file.IsDirectory && Directory.Exists(file.Path)) Directory.Delete(file.Path, true);
                            else if (File.Exists(file.Path)) File.Delete(file.Path);
                            cleaned++;
                        }
                        catch { skipped++; }
                    }
            });
            ShowToast($"Cleaned {cleaned} items" + (skipped > 0 ? $" ({skipped} skipped)" : ""));
            await _vm.ScanJunkAsync();
        }
        finally { _vm.IsBusy = false; }
    }

    private async void ScanEvidence_Click(object sender, RoutedEventArgs e) => await _vm.ScanEvidenceAsync();
    private async void CleanEvidence_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.TraceCategories.Where(c => c.IsSelected).ToList();
        if (!selected.Any()) { _vm.StatusText = "Nothing selected"; return; }
        _vm.IsBusy = true;
        try
        {
            var removed = await Task.Run(() => DeepPurge.Core.Privacy.EvidenceRemover.CleanCategories(selected));
            ShowToast($"Cleaned {removed} evidence traces");
            await _vm.ScanEvidenceAsync();
        }
        finally { _vm.IsBusy = false; }
    }

    private async void ScanEmptyFolders_Click(object sender, RoutedEventArgs e) => await _vm.ScanEmptyFoldersAsync();
    private async void DeleteEmptyFolders_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.EmptyFolders.Where(f => f.IsSelected).ToList();
        if (!selected.Any()) { _vm.StatusText = "Nothing selected"; return; }
        int deleted = 0;
        foreach (var folder in selected)
        {
            if (!SafetyGuard.IsPathSafeToDelete(folder.Path)) continue;
            try { if (Directory.Exists(folder.Path)) { Directory.Delete(folder.Path, false); deleted++; } _vm.EmptyFolders.Remove(folder); }
            catch { }
        }
        ShowToast($"Deleted {deleted} empty folders");
    }

    private async void ScanDisk_Click(object sender, RoutedEventArgs e) => await _vm.ScanDiskAsync();
    private async void DeleteLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.LargeFiles.Where(f => f.IsSelected).ToList();
        if (!selected.Any()) { _vm.StatusText = "Nothing selected"; return; }
        int deleted = 0;
        foreach (var f in selected)
        {
            if (!SafetyGuard.IsPathSafeToDelete(f.Path)) continue;
            try { File.Delete(f.Path); deleted++; _vm.LargeFiles.Remove(f); } catch { }
        }
        ShowToast($"Deleted {deleted} files");
    }

    private async void ScanContextMenu_Click(object sender, RoutedEventArgs e) => await _vm.ScanContextMenuAsync();
    private void RemoveContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.ContextMenuEntries.Where(c => c.IsSelected).ToList();
        if (!selected.Any()) { _vm.StatusText = "Nothing selected"; return; }
        var removed = DeepPurge.Core.Shell.ContextMenuCleaner.RemoveOrphanedEntries(selected);
        foreach (var item in selected) _vm.ContextMenuEntries.Remove(item);
        ShowToast($"Removed {removed} context menu entries");
    }

    private async void ScanServices_Click(object sender, RoutedEventArgs e) => await _vm.ScanServicesAsync();
    private void DisableService_Click(object sender, RoutedEventArgs e)
    {
        if (dgServices.SelectedItem is not DeepPurge.Core.Services.ServiceEntry svc) { _vm.StatusText = "Select a service"; return; }
        if (!SafetyGuard.IsServiceSafeToModify(svc.Name))
        { ShowToast($"{svc.DisplayName} is a protected Windows service", isWarning: true); return; }
        if (DeepPurge.Core.Services.ServiceScanner.DisableService(svc))
        { svc.StartType = "Disabled"; dgServices.Items.Refresh(); ShowToast($"Disabled {svc.DisplayName}"); }
        else ShowToast($"Failed to disable {svc.DisplayName}", isError: true);
    }

    private void DeleteService_Click(object sender, RoutedEventArgs e)
    {
        if (dgServices.SelectedItem is not DeepPurge.Core.Services.ServiceEntry svc) { _vm.StatusText = "Select a service"; return; }
        if (!SafetyGuard.IsServiceSafeToModify(svc.Name))
        { ShowToast($"{svc.DisplayName} is a protected Windows service", isWarning: true); return; }
        if (DeepPurge.Core.Services.ServiceScanner.DeleteService(svc))
        { _vm.Services.Remove(svc); ShowToast($"Deleted {svc.DisplayName}"); }
        else ShowToast($"Failed to delete {svc.DisplayName}", isError: true);
    }

    private async void ScanTasks_Click(object sender, RoutedEventArgs e) => await _vm.ScanTasksAsync();
    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (dgTasks.SelectedItem is not DeepPurge.Core.Tasks.ScheduledTaskInfo task) { _vm.StatusText = "Select a task"; return; }
        if (!SafetyGuard.IsTaskSafeToDelete(task.Path ?? task.Name))
        { ShowToast($"{task.Name} is a protected Windows task", isWarning: true); return; }
        if (DeepPurge.Core.Tasks.ScheduledTaskScanner.DeleteTask(task))
        { _vm.ScheduledTasks.Remove(task); ShowToast($"Deleted {task.Name}"); }
        else ShowToast($"Failed to delete {task.Name}", isError: true);
    }

    private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsBusy = true; _vm.StatusText = "Creating system restore point...";
        try
        {
            var ok = await Task.Run(() => RestorePointManager.CreateRestorePoint("DeepPurge Manual Checkpoint"));
            if (ok) { ShowToast("Restore point created"); await _vm.EnsurePanelLoadedAsync("Restore"); }
            else ShowToast("Failed to create restore point", isError: true);
        }
        finally { _vm.IsBusy = false; }
    }

    private void DeleteAutorun_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is not DeepPurge.Core.Startup.AutorunEntry entry) { _vm.StatusText = "Select an autorun entry"; return; }
        if (!SafetyGuard.IsAutorunSafeToDelete(entry.Command))
        { ShowToast($"{entry.Name} is a protected system entry", isWarning: true); return; }
        if (DeepPurge.Core.Startup.AutorunScanner.DeleteAutorun(entry))
        { _vm.Autoruns.Remove(entry); ShowToast($"Deleted {entry.Name}"); }
        else ShowToast($"Failed to delete {entry.Name}", isError: true);
    }

    private async void RemoveWindowsApp_Click(object sender, RoutedEventArgs e)
    {
        if (dgWindowsApps.SelectedItem is not DeepPurge.Core.Models.WindowsApp app) { _vm.StatusText = "Select an app"; return; }
        if (app.IsNonRemovable) { ShowToast($"{app.DisplayName} is non-removable", isWarning: true); return; }
        _vm.IsBusy = true;
        try
        {
            var ok = await DeepPurge.Core.Models.WindowsAppManager.RemoveAppAsync(app);
            if (ok) { _vm.WindowsApps.Remove(app); ShowToast($"Removed {app.DisplayName}"); }
            else ShowToast($"Failed to remove {app.DisplayName}", isError: true);
        }
        finally { _vm.IsBusy = false; }
    }

    private void RemoveBrowserExt_Click(object sender, RoutedEventArgs e)
    {
        if (dgBrowserExt.SelectedItem is not DeepPurge.Core.Browsers.BrowserExtension ext) { _vm.StatusText = "Select an extension"; return; }
        var ok = DeepPurge.Core.Browsers.BrowserExtensionScanner.RemoveExtension(ext);
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
            try { Process.Start("explorer.exe", p.InstallLocation); } catch { }
        else _vm.StatusText = "No install location available";
    }

    private void Ctx_OpenRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (dgPrograms.SelectedItem is InstalledProgram p && !string.IsNullOrEmpty(p.RegistryPath))
        {
            try
            {
                var fullPath = p.RegistryPath.Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\").Replace("HKCU\\", "HKEY_CURRENT_USER\\");
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", true);
                key?.SetValue("LastKey", fullPath);
                Process.Start("regedit.exe");
            }
            catch { _vm.StatusText = "Could not open Registry Editor"; }
        }
    }

    private void Ctx_CopyName_Click(object sender, RoutedEventArgs e)
    {
        var grid = FindParentGrid(sender);
        if (grid?.SelectedItem == null) return;
        var item = grid.SelectedItem;
        string? text = null;
        foreach (var propName in new[] { "DisplayName", "Name", "Description" })
        { var prop = item.GetType().GetProperty(propName); if (prop != null) { text = prop.GetValue(item)?.ToString(); break; } }
        if (!string.IsNullOrEmpty(text)) { Clipboard.SetText(text); ShowToast($"Copied: {text}"); }
    }

    private void Ctx_CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is DeepPurge.Core.Startup.AutorunEntry entry && !string.IsNullOrEmpty(entry.Command))
        { Clipboard.SetText(entry.Command); ShowToast("Command copied"); }
    }

    private void Ctx_OpenAutorunPath_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is DeepPurge.Core.Startup.AutorunEntry entry && !string.IsNullOrEmpty(entry.Command))
        {
            var path = entry.Command.Trim('"').Split(' ')[0];
            if (File.Exists(path)) try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }
    }

    private void Ctx_ToggleAutorun_Click(object sender, RoutedEventArgs e)
    {
        if (dgAutorun.SelectedItem is DeepPurge.Core.Startup.AutorunEntry entry)
        {
            if (DeepPurge.Core.Startup.AutorunScanner.ToggleAutorun(entry))
            { entry.IsEnabled = !entry.IsEnabled; dgAutorun.Items.Refresh(); ShowToast($"{(entry.IsEnabled ? "Enabled" : "Disabled")} {entry.Name}"); }
            else ShowToast($"Failed to toggle {entry.Name}", isError: true);
        }
    }

    private void Ctx_OpenFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (dgEmptyFolders.SelectedItem is DeepPurge.Core.FileSystem.EmptyFolderInfo folder)
        {
            var parent = System.IO.Path.GetDirectoryName(folder.Path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                try { Process.Start("explorer.exe", parent); } catch { }
        }
    }

    private void Ctx_OpenExtFolder_Click(object sender, RoutedEventArgs e)
    {
        if (dgBrowserExt.SelectedItem is DeepPurge.Core.Browsers.BrowserExtension ext && !string.IsNullOrEmpty(ext.Path))
            try { Process.Start("explorer.exe", ext.Path); } catch { }
    }

    private void Ctx_OpenServicesMsc_Click(object sender, RoutedEventArgs e)
    { try { Process.Start("services.msc"); } catch { } }

    private void Ctx_OpenTaskScheduler_Click(object sender, RoutedEventArgs e)
    { try { Process.Start("taskschd.msc"); } catch { } }

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
            var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var programs = _vm.Programs.ToList();
            switch (ext)
            {
                case ".csv": ProgramExporter.ExportToCsv(programs, dlg.FileName); break;
                case ".json": ProgramExporter.ExportToJson(programs, dlg.FileName); break;
                default: ProgramExporter.ExportToHtml(programs, dlg.FileName); break;
            }
            ShowToast($"Exported {programs.Count} programs");
            Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
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
            while (parent != null) { parent = VisualTreeHelper.GetParent(parent); if (parent is DataGrid g) return g; }
        }
        return _currentPanel switch
        {
            "Programs" => dgPrograms, "Junk" => dgJunk, "Evidence" => dgEvidence,
            "EmptyFolders" => dgEmptyFolders, "ContextMenu" => dgContextMenu,
            "Services" => dgServices, "Tasks" => dgTasks, "Restore" => dgRestore, _ => null
        };
    }

    private InstalledProgram? GetSelectedProgram() =>
        dgPrograms.SelectedItem as InstalledProgram ?? _vm.FilteredPrograms.FirstOrDefault(p => p.IsSelected);

    private ScanMode GetScanMode()
    {
        if (rbSafe?.IsChecked == true) return ScanMode.Safe;
        if (rbAdvanced?.IsChecked == true) return ScanMode.Advanced;
        return ScanMode.Moderate;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _vm.SearchFilter = txtSearch.Text;
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private void Programs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (dgPrograms.SelectedItem is InstalledProgram p) _vm.StatusText = $"{p.DisplayName}  |  {p.Publisher}  |  {p.DisplayVersion}  |  {p.InstallLocation}"; }

    private void Programs_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (dgPrograms.SelectedItem is InstalledProgram p && !string.IsNullOrEmpty(p.InstallLocation)) try { Process.Start("explorer.exe", p.InstallLocation); } catch { } }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    { var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the program's install folder" }; if (dlg.ShowDialog() == true) txtForcedPath.Text = dlg.FolderName; }

    protected override void OnClosing(CancelEventArgs e) { _vm.CancelOperation(); base.OnClosing(e); }
}
