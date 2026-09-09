namespace AudioDock.Windows.Tests;

/// <summary>
/// Deterministic coverage for the startup registration read-back contract. Uses a scripted
/// backend; it neither touches a registry nor runs Windows startup code, so it does not
/// establish physical Windows behavior.
/// </summary>
public sealed class WindowsStartupRegistrationTests
{
    private const string Command = "\"C:\\Apps\\AudioDock\\AudioDock.App.exe\"";

    [Fact]
    public void ObserveReportsAbsentWhenNothingIsRegistered()
    {
        var coordinator = new StartupRegistrationCoordinator(new ScriptedStartupBackend());

        StartupRegistrationState state = coordinator.Observe();

        Assert.Equal(StartupRegistrationObservation.NotRegistered, state.Observation);
        Assert.Null(state.Command);
    }

    [Fact]
    public void ObserveReportsRegisteredCommandWhenAnEntryExists()
    {
        var backend = new ScriptedStartupBackend { Command = Command };
        StartupRegistrationState state = new StartupRegistrationCoordinator(backend).Observe();

        Assert.Equal(StartupRegistrationObservation.Registered, state.Observation);
        Assert.Equal(Command, state.Command);
        Assert.Equal(0, backend.WriteCount);
        Assert.Equal(0, backend.RemoveCount);
    }

    [Fact]
    public void EnsureEnabledWritesAndVerifiesByReadBack()
    {
        var backend = new ScriptedStartupBackend();
        StartupRegistrationResult result = new StartupRegistrationCoordinator(backend).EnsureEnabled(Command);

        Assert.True(result.Verified);
        Assert.Null(result.Detail);
        Assert.Equal(1, backend.WriteCount);
        Assert.Equal(Command, backend.Command);
        Assert.Equal(StartupRegistrationObservation.Registered, result.Observed.Observation);
    }

    [Fact]
    public void EnsureDisabledRemovesEntryAndVerifiesAbsence()
    {
        var backend = new ScriptedStartupBackend { Command = Command };
        StartupRegistrationResult result = new StartupRegistrationCoordinator(backend).EnsureDisabled();

        Assert.True(result.Verified);
        Assert.Null(result.Detail);
        Assert.Equal(1, backend.RemoveCount);
        Assert.Null(backend.Command);
        Assert.Equal(StartupRegistrationObservation.NotRegistered, result.Observed.Observation);
    }

    [Fact]
    public void EnsureDisabledOnMissingEntryStillVerifiesAbsentState()
    {
        var backend = new ScriptedStartupBackend();
        StartupRegistrationResult result = new StartupRegistrationCoordinator(backend).EnsureDisabled();

        Assert.True(result.Verified);
        Assert.Equal(StartupRegistrationObservation.NotRegistered, result.Observed.Observation);
    }

    [Fact]
    public void EnsureEnabledReportsUnverifiedWhenStoredCommandDiverges()
    {
        var backend = new ScriptedStartupBackend { StoreCallback = command => Command + " --injected" };
        StartupRegistrationResult result = new StartupRegistrationCoordinator(backend).EnsureEnabled(Command);

        Assert.False(result.Verified);
        Assert.NotNull(result.Detail);
        Assert.NotEqual(Command, result.Observed.Command);
    }

    [Fact]
    public void EnsureDisabledReportsUnverifiedWhenEntrySurvivesRemoval()
    {
        var backend = new ScriptedStartupBackend
        {
            Command = Command,
            RemovalRemoves = false,
        };
        StartupRegistrationResult result = new StartupRegistrationCoordinator(backend).EnsureDisabled();

        Assert.False(result.Verified);
        Assert.NotNull(result.Detail);
        Assert.Equal(StartupRegistrationObservation.Registered, result.Observed.Observation);
    }

    private sealed class ScriptedStartupBackend : IStartupRegistrationBackend
    {
        public string? Command { get; set; }

        public Func<string, string>? StoreCallback { get; init; }

        public bool RemovalRemoves { get; init; } = true;

        public int WriteCount { get; private set; }

        public int RemoveCount { get; private set; }

        public string? ReadCommand() => Command;

        public void WriteCommand(string commandLine)
        {
            WriteCount++;
            Command = StoreCallback is null ? commandLine : StoreCallback(commandLine);
        }

        public void Remove()
        {
            RemoveCount++;
            if (RemovalRemoves)
            {
                Command = null;
            }
        }
    }
}
