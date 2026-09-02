using System.Text.Json;
using AudioDock.Core.Models;
using AudioDock.Core.Persistence;

namespace AudioDock.Core.Tests.Persistence;

public sealed class JsonSceneStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"AudioDockTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task MigratesCommittedV1ArrayAndSavesExplicitVersionedEnvelope()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        Guid id = Guid.NewGuid();
        await File.WriteAllTextAsync(path, $$"""[{"schemaVersion":1,"id":"{{id}}","name":"Desk","roleTargets":[],"endpointRules":[],"applicationRules":[]}]""");
        var store = new JsonSceneStore(path);

        AudioScene migrated = Assert.Single(await store.LoadAsync());
        await store.SaveAsync([migrated]);

        using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, saved.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(id, saved.RootElement.GetProperty("scenes")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ValidPrimaryWinsOverStaleTemporaryFile()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        var primary = new AudioScene(1, Guid.NewGuid(), "Committed");
        var store = new JsonSceneStore(path);
        await store.SaveAsync([primary]);
        await File.WriteAllTextAsync(path + ".tmp", $$"""{"schemaVersion":1,"scenes":[{"schemaVersion":1,"id":"{{Guid.NewGuid()}}","name":"Stale","roleTargets":[],"endpointRules":[],"applicationRules":[]}]}""");

        Assert.Equal("Committed", Assert.Single(await store.LoadAsync()).Name);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ValidTemporaryRecoversOnlyWhenPrimaryIsInvalid()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        await File.WriteAllTextAsync(path, "truncated");
        await File.WriteAllTextAsync(path + ".tmp", $$"""{"schemaVersion":1,"scenes":[{"schemaVersion":1,"id":"{{Guid.NewGuid()}}","name":"Recovered","roleTargets":[],"endpointRules":[],"applicationRules":[]}]}""");

        Assert.Equal("Recovered", Assert.Single(await new JsonSceneStore(path).LoadAsync()).Name);
    }

    [Fact]
    public async Task FutureVersionIsRejectedWithoutReplacingPrimary()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        string future = "{\"schemaVersion\":99,\"scenes\":[]}";
        await File.WriteAllTextAsync(path, future);

        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(async () => await new JsonSceneStore(path).LoadAsync());
        Assert.Equal(future, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StructurallyIncompletePrimaryIsRecoveredFromSupportedTemporaryFile()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1}");
        await File.WriteAllTextAsync(path + ".tmp", $$"""{"schemaVersion":1,"scenes":[{"schemaVersion":1,"id":"{{Guid.NewGuid()}}","name":"Recovered","roleTargets":[],"endpointRules":[],"applicationRules":[]}]}""");

        Assert.Equal("Recovered", Assert.Single(await new JsonSceneStore(path).LoadAsync()).Name);
    }

    [Fact]
    public async Task CompleteFuturePrimaryWinsOverSupportedTemporaryFile()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        string future = "{\"schemaVersion\":99,\"scenes\":[]}";
        await File.WriteAllTextAsync(path, future);
        await File.WriteAllTextAsync(path + ".tmp", "{\"schemaVersion\":1,\"scenes\":[]}");

        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(async () => await new JsonSceneStore(path).LoadAsync());
        Assert.Equal(future, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task FutureTemporaryFileDoesNotReplaceInvalidPrimary()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        await File.WriteAllTextAsync(path, "invalid");
        await File.WriteAllTextAsync(path + ".tmp", "{\"schemaVersion\":99,\"scenes\":[]}");

        await Assert.ThrowsAnyAsync<JsonException>(async () => await new JsonSceneStore(path).LoadAsync());
        Assert.Equal("invalid", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConcurrentSavesAlwaysLeaveOneCompleteDocument()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        var store = new JsonSceneStore(path);
        AudioScene[] scenes = Enumerable.Range(0, 24).Select(index => new AudioScene(1, Guid.NewGuid(), $"Scene {index}")).ToArray();

        await Task.WhenAll(scenes.Select(scene => store.SaveAsync([scene]).AsTask()));

        AudioScene saved = Assert.Single(await store.LoadAsync());
        Assert.Contains(scenes, scene => scene.Id == saved.Id);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task MutationSerializesReadCheckWriteWithSavesAcrossInstances()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        var first = new JsonSceneStore(path);
        var second = new JsonSceneStore(Path.Combine(directory, ".", "scenes.json"));
        var original = new AudioScene(1, Guid.NewGuid(), "Original");
        var saved = new AudioScene(1, Guid.NewGuid(), "Saved after mutation");
        await first.SaveAsync([original]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task mutation = Task.Run(async () => await first.MutateAsync(current =>
        {
            Assert.Equal(original.Id, Assert.Single(current).Id);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return current;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task save = second.SaveAsync([saved]).AsTask();
        Assert.False(save.IsCompleted);
        release.Set();
        await Task.WhenAll(mutation, save);

        Assert.Equal(saved.Id, Assert.Single(await first.LoadAsync()).Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
