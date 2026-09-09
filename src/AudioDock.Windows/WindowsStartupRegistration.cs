using Microsoft.Win32;

namespace AudioDock.Windows;

/// <summary>
/// Storage boundary for the per-user sign-in startup entry. Kept behind an interface
/// so enable/disable/read-back verification logic is testable without touching a registry.
/// </summary>
public interface IStartupRegistrationBackend
{
    /// <summary>Returns the registered launch command, or <see langword="null"/> when absent.</summary>
    string? ReadCommand();

    void WriteCommand(string commandLine);

    void Remove();
}

/// <summary>
/// Writes the per-user sign-in startup entry under the current user's Run key.
/// The entry is visible in the standard Windows startup UI, reversible, and never written
/// without an explicit user action. State is always re-read after every mutation.
/// The key path and value name are injectable so opt-in integration tests can exercise a
/// throwaway probe location instead of the production startup entry.
/// </summary>
public sealed class RegistryStartupBackend(
    string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string valueName = "AudioDock") : IStartupRegistrationBackend
{
    public string? ReadCommand()
    {
        RequireWindows();
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void WriteCommand(string commandLine)
    {
        RequireWindows();
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
        key.SetValue(valueName, commandLine, RegistryValueKind.ExpandString);
    }

    public void Remove()
    {
        RequireWindows();
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Startup registration requires Windows; no state was changed.");
        }
    }
}

public enum StartupRegistrationObservation
{
    /// <summary>No startup entry exists at the owned location.</summary>
    NotRegistered,

    /// <summary>A startup entry exists at the owned location.</summary>
    Registered,
}

public sealed record StartupRegistrationState(StartupRegistrationObservation Observation, string? Command);

public sealed record StartupRegistrationResult(bool Verified, StartupRegistrationState Observed, string? Detail);

/// <summary>
/// Applies and verifies the per-user sign-in startup entry. Every mutation is followed by an
/// observable read-back; results report exactly what was observed instead of assuming success.
/// </summary>
public sealed class StartupRegistrationCoordinator(IStartupRegistrationBackend backend)
{
    public StartupRegistrationState Observe()
    {
        string? command = backend.ReadCommand();
        return new(command is null ? StartupRegistrationObservation.NotRegistered : StartupRegistrationObservation.Registered, command);
    }

    public StartupRegistrationResult EnsureEnabled(string commandLine)
    {
        backend.WriteCommand(commandLine);
        StartupRegistrationState observed = Observe();
        bool verified = observed.Observation == StartupRegistrationObservation.Registered
            && string.Equals(observed.Command, commandLine, StringComparison.Ordinal);
        return new(verified, observed, verified
            ? null
            : "Startup write did not read back as the requested command; the observed entry is reported unchanged.");
    }

    public StartupRegistrationResult EnsureDisabled()
    {
        backend.Remove();
        StartupRegistrationState observed = Observe();
        bool verified = observed.Observation == StartupRegistrationObservation.NotRegistered;
        return new(verified, observed, verified
            ? null
            : "Startup removal did not read back as absent; the observed entry is reported unchanged.");
    }
}
