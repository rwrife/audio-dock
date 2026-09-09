using Microsoft.Win32;

namespace AudioDock.Windows.Tests;

/// <summary>
/// Opt-in Windows-only round trip against the real registry backend. It writes to a throwaway
/// probe key (never the production startup entry), always removes the probe key in
/// <c>finally</c>, and reports restoration failure. Passing here still does not prove tray
/// behavior, startup UI visibility, or uninstall ownership — those need physical Windows evidence.
/// </summary>
public sealed class WindowsStartupRegistrationIntegrationTests
{
    private const string ProbeKeyPath = @"Software\AudioDockTestProbe";
    private const string ProbeValueName = "AudioDockProbe";

    [WindowsStartupRegistrationFact]
    public void BackendRoundTripsWriteReadRemoveAgainstProbeKey()
    {
        Exception? testFailure = null;
        string restorationFailure = string.Empty;
        try
        {
            var backend = new RegistryStartupBackend(ProbeKeyPath, ProbeValueName);
            var coordinator = new StartupRegistrationCoordinator(backend);
            string command = $"\"{Environment.ProcessPath}\" --probe-{Guid.NewGuid():N}";

            Assert.Equal(StartupRegistrationObservation.NotRegistered, coordinator.Observe().Observation);

            StartupRegistrationResult enabled = coordinator.EnsureEnabled(command);
            Assert.True(enabled.Verified, enabled.Detail);
            Assert.Equal(command, enabled.Observed.Command);

            StartupRegistrationResult disabled = coordinator.EnsureDisabled();
            Assert.True(disabled.Verified, disabled.Detail);
            Assert.Equal(StartupRegistrationObservation.NotRegistered, disabled.Observed.Observation);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(ProbeKeyPath, throwOnMissingSubKey: false);
            }
            catch (Exception exception)
            {
                restorationFailure = $"Probe startup key cleanup failed: {exception.Message}";
            }
        }

        if (restorationFailure.Length > 0)
        {
            throw new InvalidOperationException(restorationFailure);
        }

        if (testFailure is not null)
        {
            throw testFailure;
        }
    }
}

internal sealed class WindowsStartupRegistrationFactAttribute : FactAttribute
{
    public WindowsStartupRegistrationFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Startup registration integration requires a Windows host.";
        }
        else if (!StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("AUDIO_DOCK_STARTUP_TEST"),
            "1"))
        {
            Skip = "Set AUDIO_DOCK_STARTUP_TEST=1 to opt in to startup registration integration.";
        }
    }
}
