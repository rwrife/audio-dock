using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioDock.App.ViewModels;
using AudioDock.Core.Models;
using AudioDock.Core.Persistence;
using AudioDock.Core.Workflow;
using AudioDock.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AudioDock.App;

public partial class MainWindow : Window, IDisposable
{
    private const int HotkeyId = 0xAD04;
    private const int WmHotkey = 0x0312;
    private readonly WindowsAudioInventory adapter;
    private readonly MainViewModel viewModel;
    private readonly Forms.NotifyIcon tray;
    private readonly string dataRoot;
    private readonly JsonSettingsStore settingsStore;
    private readonly SceneTransferService transfers;
    private readonly LocalDataMaintenance maintenance;
    private readonly LocalDiagnosticStore diagnostics;
    private HwndSource? source;
    private bool hotkeyRegistered;
    private bool hotkeysPaused;
    private string? hotkeyProblem;
    private bool loadingSettings = true;
    private bool disposed;
    private bool closePending;
    private bool closeReady;
    private Task? loadingTask;

    public MainWindow()
    {
        InitializeComponent();
        dataRoot = AudioDockDataPaths.DefaultRoot;
        settingsStore = new(AudioDockDataPaths.Settings(dataRoot));
        var sceneStore = new JsonSceneStore(AudioDockDataPaths.Scenes(dataRoot));
        diagnostics = new(AudioDockDataPaths.Diagnostics(dataRoot));
        transfers = new(sceneStore, diagnostics);
        maintenance = new(dataRoot, diagnostics, sceneStore);
        adapter = new WindowsAudioInventory();
        var workflow = new SceneWorkflow(adapter, sceneStore, new JsonActivityStore(AudioDockDataPaths.Activity(dataRoot)),
            () => AllowExecutablePaths.IsChecked == true, diagnostics);
        viewModel = new(workflow);
        DataContext = viewModel;
        tray = new Forms.NotifyIcon
        {
            Text = "Audio Dock",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowMainWindow();
        viewModel.Scenes.CollectionChanged += (_, _) => BuildTrayMenu();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        DataLocationText.Text = $"Data location: {maintenance.DataLocation}";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closePending) return;

        closePending = true;
        try
        {
            await viewModel.CancelActiveOperationAndWaitAsync();
            if (loadingTask is not null) await loadingTask;
            await Dispatcher.Yield(DispatcherPriority.Background);
            closeReady = true;
            Close();
        }
        finally
        {
            closePending = false;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        source = (HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(WindowProcedure);
        LoadSettings();
        loadingTask = FinishLoadingAsync();
        await loadingTask;
    }

    private async Task FinishLoadingAsync()
    {
        await viewModel.InitializeAsync();
        if (closePending) return;
        BuildTrayMenu();
        loadingSettings = false;
        ConfigureHotkey();
        SceneList.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DisableHotkey();
        source?.RemoveHook(WindowProcedure);
        tray.Visible = false;
        tray.Dispose();
        viewModel.Dispose();
        adapter.Dispose();
        GC.SuppressFinalize(this);
    }

    private void BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show Audio Dock", null, (_, _) => ShowMainWindow());
        var apply = new Forms.ToolStripMenuItem("Apply named scene");
        foreach (AudioScene scene in viewModel.Scenes)
        {
            apply.DropDownItems.Add(scene.Name, null, async (_, _) => await viewModel.ApplyNamedAsync(scene));
        }

        if (viewModel.Scenes.Count == 0) apply.DropDownItems.Add("No scenes available").Enabled = false;
        menu.Items.Add(apply);
        menu.Items.Add("Undo latest apply", null, async (_, _) => await viewModel.UndoLatestAsync());
        TrayHotkeyState trayState = CurrentTrayState();
        var pause = new Forms.ToolStripMenuItem(trayState.PauseText, null, (_, _) => ToggleHotkeyPause())
        {
            Checked = trayState.PauseChecked,
            CheckOnClick = false,
            AccessibleName = $"{trayState.PauseText}. {trayState.StatusText}",
        };
        menu.Items.Add(pause);
        menu.Items.Add(new Forms.ToolStripMenuItem(trayState.StatusText) { Enabled = false, AccessibleName = $"Hotkey status: {trayState.StatusText}" });
        menu.Items.Add("Settings", null, (_, _) =>
        {
            ShowMainWindow();
            WorkflowTabs.SelectedIndex = 2;
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        Forms.ContextMenuStrip? previous = tray.ContextMenuStrip;
        tray.ContextMenuStrip = menu;
        previous?.Dispose();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HotkeySettingChanged(object sender, RoutedEventArgs e)
    {
        if (loadingSettings || !IsLoaded) return;
        ConfigureHotkey();
        SaveSettings();
    }

    private void DisableHotkeysClick(object sender, RoutedEventArgs e)
    {
        HotkeyEnabled.IsChecked = false;
        hotkeysPaused = false;
        DisableHotkey();
        SaveSettings();
        HotkeyStatus.Text = "Hotkeys disabled. This can be re-enabled at any time.";
    }

    private void ToggleHotkeyPause()
    {
        hotkeysPaused = !hotkeysPaused;
        if (hotkeysPaused) DisableHotkey(); else ConfigureHotkey();
        if (hotkeysPaused) HotkeyStatus.Text = "Hotkeys paused from the tray.";
        UpdateTrayFeedback(HotkeyStatus.Text);
    }

    private void ConfigureHotkey()
    {
        DisableHotkey();
        if (HotkeyEnabled.IsChecked != true || hotkeysPaused || source is null)
        {
            HotkeyStatus.Text = hotkeysPaused ? "Hotkeys paused." : "Hotkeys disabled.";
            hotkeyProblem = null;
            BuildTrayMenu();
            return;
        }

        string keyText = HotkeyKey.Text.Trim().ToUpperInvariant();
        if (keyText.Length != 1 || keyText[0] is < 'A' or > 'Z')
        {
            HotkeyStatus.Text = "Conflict/error: choose one letter A through Z.";
            hotkeyProblem = "choose a letter A through Z";
            UpdateTrayFeedback(HotkeyStatus.Text);
            return;
        }

        uint modifiers = HotkeyModifiers.SelectedIndex switch { 1 => 0x0002u | 0x0004u, 2 => 0x0001u | 0x0004u, _ => 0x0002u | 0x0001u };
        hotkeyRegistered = RegisterHotKey(source.Handle, HotkeyId, modifiers | 0x4000u, keyText[0]);
        HotkeyStatus.Text = hotkeyRegistered
            ? $"Registered {ModifierLabel()}+{keyText}. Applies the selected named scene after preview validation."
            : $"Registration conflict: {ModifierLabel()}+{keyText} is unavailable. Disable it or choose another chord.";
        hotkeyProblem = hotkeyRegistered ? null : $"{ModifierLabel()}+{keyText} is unavailable";
        UpdateTrayFeedback(HotkeyStatus.Text);
    }

    private TrayHotkeyState CurrentTrayState() => new(HotkeyEnabled.IsChecked == true, hotkeysPaused, hotkeyRegistered, hotkeyProblem);

    private void UpdateTrayFeedback(string detail)
    {
        BuildTrayMenu();
        TrayHotkeyState state = CurrentTrayState();
        tray.Text = $"Audio Dock — {state.StatusText}"[..Math.Min(63, $"Audio Dock — {state.StatusText}".Length)];
        tray.BalloonTipTitle = state.StatusText;
        tray.BalloonTipText = detail;
        tray.ShowBalloonTip(2500);
    }

    private string ModifierLabel() => HotkeyModifiers.SelectedIndex switch { 1 => "Control+Shift", 2 => "Alt+Shift", _ => "Control+Alt" };

    private void DisableHotkey()
    {
        if (hotkeyRegistered && source is not null) UnregisterHotKey(source.Handle, HotkeyId);
        hotkeyRegistered = false;
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (viewModel.SelectedScene is AudioScene scene) _ = viewModel.ApplyNamedAsync(scene);
            else HotkeyStatus.Text = "Hotkey received, but no named scene is selected.";
        }
        return IntPtr.Zero;
    }

    private void LoadSettings()
    {
        try
        {
            AppSettings settings = settingsStore.LoadAsync().AsTask().GetAwaiter().GetResult();
            HotkeyEnabled.IsChecked = settings.HotkeyEnabled;
            HotkeyModifiers.SelectedIndex = settings.HotkeyModifierChoice;
            HotkeyKey.Text = settings.HotkeyKey;
            AllowExecutablePaths.IsChecked = settings.AllowExecutablePathMatching;
        }
        catch (Exception exception)
        {
            HotkeyStatus.Text = $"Settings could not be loaded: {exception.Message}";
        }
    }

    private async void SaveSettings()
    {
        try
        {
            await settingsStore.SaveAsync(new(AppSettings.CurrentSchemaVersion,
                HotkeyEnabled.IsChecked == true, HotkeyModifiers.SelectedIndex, HotkeyKey.Text.Trim().ToUpperInvariant(),
                AllowExecutablePaths.IsChecked == true));
        }
        catch (Exception exception)
        {
            HotkeyStatus.Text = $"Settings could not be saved: {exception.Message}";
        }
    }

    private async void ExportScenesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = "Audio Dock scenes (*.json)|*.json", FileName = "AudioDock-scenes.json" };
            if (dialog.ShowDialog(this) != true) return;
            SceneExportResult result = await transfers.ExportAsync(dialog.FileName);
            MessageBox.Show(this, $"Exported {result.SceneCount} scene(s). {result.PortabilityWarning}", "Export complete");
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BackupScenesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = "Audio Dock backup (*.json)|*.json", FileName = "AudioDock-backup.json" };
            if (dialog.ShowDialog(this) != true) return;
            SceneExportResult result = await transfers.BackupAsync(dialog.FileName);
            MessageBox.Show(this, $"Backed up {result.SceneCount} scene(s). {result.PortabilityWarning}", "Backup complete");
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ImportScenesClick(object sender, RoutedEventArgs e)
    {
        try { await PreviewAndImportAsync("Import scenes"); }
        catch (Exception exception) { MessageBox.Show(this, $"Import could not start; no data or audio state changed. {exception.Message}", "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RestoreScenesClick(object sender, RoutedEventArgs e)
    {
        try { await PreviewAndImportAsync("Restore backup"); }
        catch (Exception exception) { MessageBox.Show(this, $"Restore could not start; no data or audio state changed. {exception.Message}", "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task PreviewAndImportAsync(string title)
    {
        bool committed = false;
        try
        {
            var dialog = new OpenFileDialog { Filter = "Audio Dock JSON (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            SceneImportPreview preview = await transfers.PreviewImportAsync(dialog.FileName);
            MessageBoxResult choice = MessageBox.Show(this,
                $"Validated {preview.Incoming.Count} scene(s); {preview.Conflicts.Count} ID conflict(s). {preview.PortabilityWarning}\n\nYes: merge (incoming wins conflicts)\nNo: replace all scenes\nCancel: make no changes",
                $"{title} conflict preview", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return;
            await transfers.ApplyImportAsync(preview, choice == MessageBoxResult.Yes ? SceneConflictChoice.Merge : SceneConflictChoice.Replace);
            committed = true;
            viewModel.Scenes.Clear();
            foreach (AudioScene scene in await new JsonSceneStore(AudioDockDataPaths.Scenes(dataRoot)).LoadAsync()) viewModel.Scenes.Add(scene);
            viewModel.SelectedScene = viewModel.Scenes.FirstOrDefault();
            MessageBox.Show(this, "Scenes stored. No audio settings were changed; preview a scene separately before applying it.", title);
        }
        catch (Exception exception)
        {
            string message = committed
                ? $"Scenes were stored, but the on-screen list could not be refreshed. Reopen Audio Dock before applying a scene. {exception.Message}"
                : $"No data or audio state was changed. {exception.Message}";
            MessageBox.Show(this, message, $"{title} failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearScenesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MessageBox.Show(this, "Permanently clear every stored scene? This does not change audio.", "Clear all scenes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await maintenance.ClearScenesAsync();
            viewModel.Scenes.Clear();
            viewModel.SelectedScene = null;
            MessageBox.Show(this, "All stored scenes were cleared.", "Scenes cleared");
        }
        catch (Exception exception) { MessageBox.Show(this, $"Scenes could not be cleared: {exception.Message}", "Clear scenes failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void ClearActivityClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await maintenance.ClearActivityAsync();
            viewModel.Activity.Clear();
            viewModel.SelectedActivity = null;
            MessageBox.Show(this, "Stored activity was cleared.", "Activity cleared");
        }
        catch (Exception exception) { MessageBox.Show(this, $"Activity could not be cleared: {exception.Message}", "Clear activity failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void ClearDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try { await diagnostics.ClearAsync(); MessageBox.Show(this, "Local diagnostics cleared.", "Diagnostics"); }
        catch (Exception exception) { MessageBox.Show(this, $"Diagnostics could not be cleared: {exception.Message}", "Clear diagnostics failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void InspectDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<DiagnosticEvent> items = await diagnostics.InspectAsync();
            MessageBox.Show(this, items.Count == 0 ? "No local diagnostics are stored." : string.Join(Environment.NewLine, items.Take(20).Select(item => $"{item.OccurredAt:u} {item.Level} {item.Event}: {item.Detail}")), "Local redacted diagnostics");
        }
        catch (Exception exception) { MessageBox.Show(this, $"Diagnostics could not be inspected: {exception.Message}", "Inspect diagnostics failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void OpenDataLocationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dataRoot) { UseShellExecute = true });
        }
        catch (Exception exception) { MessageBox.Show(this, $"The data folder could not be opened: {exception.Message}", "Open folder failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

}
