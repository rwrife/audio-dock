namespace AudioDock.App.ViewModels;

public sealed record TrayHotkeyState(bool Enabled, bool Paused, bool Registered, string? Problem)
{
    public string PauseText => Paused ? "Resume hotkeys" : "Pause hotkeys";
    public bool PauseChecked => Paused;
    public string StatusText => Paused
        ? "Hotkeys paused"
        : !Enabled ? "Hotkeys disabled"
        : Registered ? "Hotkey registered"
        : $"Hotkey unavailable{(string.IsNullOrWhiteSpace(Problem) ? string.Empty : $": {Problem}")}";
}
