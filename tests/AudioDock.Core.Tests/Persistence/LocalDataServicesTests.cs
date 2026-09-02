using AudioDock.Core.Models;
using AudioDock.Core.Persistence;

namespace AudioDock.Core.Tests.Persistence;

public sealed class LocalDataServicesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"AudioDockLocal-{Guid.NewGuid():N}");

    [Fact]
    public async Task SettingsAreVersionedAndAtomicallyRecoveredWithoutStaleOverwrite()
    {
        Directory.CreateDirectory(directory);
        string path = AudioDockDataPaths.Settings(directory);
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(AppSettings.Defaults with { HotkeyEnabled = true });
        await File.WriteAllTextAsync(path + ".tmp", "{\"schemaVersion\":1,\"hotkeyEnabled\":false,\"hotkeyModifierChoice\":0,\"hotkeyKey\":\"X\"}");

        Assert.True((await store.LoadAsync()).HotkeyEnabled);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task DiagnosticsRedactPathsExcludeContentByApiAndRemainBounded()
    {
        Directory.CreateDirectory(directory);
        string path = AudioDockDataPaths.Diagnostics(directory);
        var store = new LocalDiagnosticStore(path, maximumBytes: 700, maximumAge: TimeSpan.FromDays(1));
        await store.AppendAsync(new(DateTimeOffset.UtcNow.AddDays(-2), "Info", "old", "C:\\Users\\Me\\old.exe"));
        for (int index = 0; index < 20; index++)
            await store.AppendAsync(new(DateTimeOffset.UtcNow, "Info", "match", $"Executable C:\\Users\\Me\\player{index}.exe selected"));

        string raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("Users", raw);
        Assert.DoesNotContain("old", raw);
        Assert.InRange(new FileInfo(path).Length, 1, 700);
        Assert.All(await store.InspectAsync(), item => Assert.Contains("[path redacted]", item.Detail));
    }

    [Fact]
    public async Task DiagnosticsRedactUncAndWindowsDevicePaths()
    {
        string path = AudioDockDataPaths.Diagnostics(directory);
        var store = new LocalDiagnosticStore(path);
        await store.AppendAsync(new(DateTimeOffset.UtcNow, "Info", "paths",
            @"Opened \\server\share\private\file.exe and \\?\C:\Users\Name\file.exe"));

        string raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("server", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file.exe", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[path redacted]", raw);
    }

    [Fact]
    public async Task DiagnosticsOperationsAreCoordinatedAcrossNormalizedPathAndInstances()
    {
        Directory.CreateDirectory(directory);
        string path = AudioDockDataPaths.Diagnostics(directory);
        var first = new LocalDiagnosticStore(path, maximumBytes: 1024 * 1024);
        var second = new LocalDiagnosticStore(Path.Combine(directory, ".", "diagnostics.jsonl"), maximumBytes: 1024 * 1024);

        Task[] appends = Enumerable.Range(0, 100).Select(index =>
            (index % 2 == 0 ? first : second).AppendAsync(new(DateTimeOffset.UtcNow, "Info", $"event-{index}", "safe")).AsTask()).ToArray();
        Task<IReadOnlyList<DiagnosticEvent>> inspection = first.InspectAsync().AsTask();
        await Task.WhenAll(appends);
        Assert.InRange((await inspection).Count, 0, 100);
        Assert.Equal(100, (await second.InspectAsync()).Count);

        await first.ClearAsync();
        Assert.Empty(await second.InspectAsync());
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ClearScenesWaitsForCoordinatedSaveAndLeavesPersistedStoreEmpty()
    {
        Directory.CreateDirectory(directory);
        string path = AudioDockDataPaths.Scenes(directory);
        var store = new JsonSceneStore(path);
        await store.SaveAsync([new AudioScene(1, Guid.NewGuid(), "Initial")]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task save = Task.Run(async () => await new JsonSceneStore(Path.Combine(directory, ".", "scenes.json")).MutateAsync(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return [new AudioScene(1, Guid.NewGuid(), "Concurrent")];
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task clear = new LocalDataMaintenance(directory, sceneStore: store).ClearScenesAsync().AsTask();
        Assert.False(clear.IsCompleted);
        release.Set();
        await Task.WhenAll(save, clear);

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task LegacyUnversionedHotkeySettingsMigrateToVersionedDocument()
    {
        Directory.CreateDirectory(directory);
        string path = AudioDockDataPaths.Settings(directory);
        await File.WriteAllTextAsync(path, "{\"Enabled\":true,\"ModifierChoice\":2,\"Key\":\"K\"}");

        AppSettings settings = await new JsonSettingsStore(path).LoadAsync();

        Assert.True(settings.HotkeyEnabled);
        Assert.Equal(2, settings.HotkeyModifierChoice);
        Assert.Equal("K", settings.HotkeyKey);
        Assert.False(settings.AllowExecutablePathMatching);
        using System.Text.Json.JsonDocument migrated = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData(-1, "D")]
    [InlineData(3, "D")]
    [InlineData(0, "")]
    [InlineData(0, "F1")]
    [InlineData(0, "7")]
    public async Task SettingsSaveRejectsInvalidHotkeys(int modifier, string key)
    {
        var store = new JsonSettingsStore(AudioDockDataPaths.Settings(directory));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(AppSettings.Defaults with
        {
            HotkeyModifierChoice = modifier,
            HotkeyKey = key,
        }));
    }

    [Fact]
    public async Task TransferAndMaintenanceWritePrivacySafeProductionDiagnostics()
    {
        Directory.CreateDirectory(directory);
        string diagnosticPath = AudioDockDataPaths.Diagnostics(directory);
        var diagnostics = new LocalDiagnosticStore(diagnosticPath);
        var store = new JsonSceneStore(AudioDockDataPaths.Scenes(directory));
        var secret = new AudioScene(1, Guid.NewGuid(), "Private scene", endpointRules:
            [new(new(AudioDirection.Render, ExactId: "secret-target"), new(0.5))]);
        await store.SaveAsync([secret]);
        string exportPath = Path.Combine(directory, "private-export.json");
        var transfer = new SceneTransferService(store, diagnostics);

        await transfer.ExportAsync(exportPath);
        await new LocalDataMaintenance(directory, diagnostics).ClearActivityAsync();

        string raw = await File.ReadAllTextAsync(diagnosticPath);
        Assert.Contains("scene_export", raw);
        Assert.Contains("activity_clear", raw);
        Assert.DoesNotContain("Private scene", raw);
        Assert.DoesNotContain("secret-target", raw);
        Assert.DoesNotContain(exportPath, raw);
        Assert.DoesNotContain("0.5", raw);
    }

    [Fact]
    public async Task DiagnosticFailureDoesNotBreakDataManagementOperation()
    {
        Directory.CreateDirectory(directory);
        var store = new JsonSceneStore(AudioDockDataPaths.Scenes(directory));
        await store.SaveAsync([new AudioScene(1, Guid.NewGuid(), "Scene")]);
        string destination = Path.Combine(directory, "export.json");

        SceneExportResult result = await new SceneTransferService(store, new ThrowingDiagnostics()).ExportAsync(destination);

        Assert.Equal(1, result.SceneCount);
        Assert.True(File.Exists(destination));
    }

    private sealed class ThrowingDiagnostics : IDiagnosticSink
    {
        public ValueTask AppendAsync(DiagnosticEvent item, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("diagnostic disk full"));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
