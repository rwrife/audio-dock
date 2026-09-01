using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioDock.App.ViewModels;
using AudioDock.Core.Models;
using AudioDock.Core.Persistence;
using AudioDock.Core.Workflow;
using AudioDock.Windows;
using Forms = System.Windows.Forms;

namespace AudioDock.App;

public partial class MainWindow : Window, IDisposable
{
    private const int HotkeyId = 0xAD04;
    private const int WmHotkey = 0x0312;
    private readonly WindowsAudioInventory adapter;
    private readonly MainViewModel viewModel;
    private readonly Forms.NotifyIcon tray;
    private readonly string settingsPath;
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
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioDock");
        settingsPath = Path.Combine(data, "settings.json");
        adapter = new WindowsAudioInventory();
        var workflow = new SceneWorkflow(adapter, new JsonSceneStore(Path.Combine(data, "scenes.json")), new JsonActivityStore(Path.Combine(data, "activity.json")));
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
            if (!File.Exists(settingsPath)) return;
            HotkeyPreferences? settings = JsonSerializer.Deserialize<HotkeyPreferences>(File.ReadAllText(settingsPath));
            if (settings is null) return;
            HotkeyEnabled.IsChecked = settings.Enabled;
            HotkeyModifiers.SelectedIndex = Math.Clamp(settings.ModifierChoice, 0, 2);
            HotkeyKey.Text = settings.Key;
        }
        catch (Exception exception)
        {
            HotkeyStatus.Text = $"Settings could not be loaded: {exception.Message}";
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new HotkeyPreferences(
                HotkeyEnabled.IsChecked == true, HotkeyModifiers.SelectedIndex, HotkeyKey.Text.Trim().ToUpperInvariant())));
        }
        catch (Exception exception)
        {
            HotkeyStatus.Text = $"Settings could not be saved: {exception.Message}";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private sealed record HotkeyPreferences(bool Enabled, int ModifierChoice, string Key);
}
