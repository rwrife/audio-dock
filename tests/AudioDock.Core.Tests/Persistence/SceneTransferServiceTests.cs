using AudioDock.Core.Models;
using AudioDock.Core.Persistence;

namespace AudioDock.Core.Tests.Persistence;

public sealed class SceneTransferServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"AudioDockTransfer-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreviewRejectsDuplicateIdsAndDoesNotChangeStoredScenes()
    {
        Directory.CreateDirectory(directory);
        string stored = Path.Combine(directory, "scenes.json");
        string import = Path.Combine(directory, "import.json");
        var existing = new AudioScene(1, Guid.NewGuid(), "Existing");
        var store = new JsonSceneStore(stored); await store.SaveAsync([existing]);
        string item = $$"""{"schemaVersion":1,"id":"{{Guid.NewGuid()}}","name":"Duplicate","roleTargets":[],"endpointRules":[],"applicationRules":[]}""";
        await File.WriteAllTextAsync(import, $$"""{"schemaVersion":1,"scenes":[{{item}},{{item}}]}""");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await new SceneTransferService(store).PreviewImportAsync(import));
        Assert.Equal(existing.Id, Assert.Single(await store.LoadAsync()).Id);
    }

    [Fact]
    public async Task ExportRedactsPathsAndRoundTripsDeterministically()
    {
        Directory.CreateDirectory(directory);
        string stored = Path.Combine(directory, "scenes.json");
        string first = Path.Combine(directory, "first.json");
        string second = Path.Combine(directory, "second.json");
        var scene = new AudioScene(1, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Player", applicationRules:
            [new(new(ExecutablePath: "C:\\Users\\Private\\player.exe", ProductName: "Player"), new(0.4))]);
        var store = new JsonSceneStore(stored); await store.SaveAsync([scene]);
        var transfer = new SceneTransferService(store);

        SceneExportResult result = await transfer.ExportAsync(first);
        Assert.True(result.PathsRedacted);
        Assert.DoesNotContain("Users", await File.ReadAllTextAsync(first));
        SceneImportPreview preview = await transfer.PreviewImportAsync(first);
        await transfer.ApplyImportAsync(preview, SceneConflictChoice.Replace);
        await transfer.ExportAsync(second);
        Assert.Equal(await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second));
    }

    [Fact]
    public async Task MergeAndReplaceAreExplicitAndHaveDifferentResults()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        string backup = Path.Combine(directory, "backup.json");
        var one = new AudioScene(1, Guid.NewGuid(), "One");
        var two = new AudioScene(1, Guid.NewGuid(), "Two");
        var store = new JsonSceneStore(path); await store.SaveAsync([one]);
        var source = new JsonSceneStore(backup); await source.SaveAsync([two]);
        var transfer = new SceneTransferService(store);
        SceneImportPreview preview = await transfer.PreviewRestoreAsync(backup);

        await transfer.ApplyImportAsync(preview, SceneConflictChoice.Merge);
        Assert.Equal(2, (await store.LoadAsync()).Count);
        preview = await transfer.PreviewRestoreAsync(backup);
        await transfer.ApplyImportAsync(preview, SceneConflictChoice.Replace);
        Assert.Equal(two.Id, Assert.Single(await store.LoadAsync()).Id);
    }

    [Theory]
    [InlineData(SceneConflictChoice.Merge)]
    [InlineData(SceneConflictChoice.Replace)]
    public async Task ApplyRejectsPreviewWhenStoredScenesChanged(SceneConflictChoice choice)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "scenes.json");
        string import = Path.Combine(directory, "import.json");
        var original = new AudioScene(1, Guid.NewGuid(), "Original");
        var intervening = new AudioScene(1, Guid.NewGuid(), "Intervening");
        var incoming = new AudioScene(1, Guid.NewGuid(), "Incoming");
        var store = new JsonSceneStore(path); await store.SaveAsync([original]);
        await new JsonSceneStore(import).SaveAsync([incoming]);
        var transfer = new SceneTransferService(store);
        SceneImportPreview preview = await transfer.PreviewImportAsync(import);
        await store.SaveAsync([intervening]);

        await Assert.ThrowsAsync<StaleSceneImportPreviewException>(async () => await transfer.ApplyImportAsync(preview, choice));
        Assert.Equal(intervening.Id, Assert.Single(await store.LoadAsync()).Id);
    }

    [Fact]
    public async Task ApplyPerformsRevisionCheckAndWriteInOneStoreMutation()
    {
        var original = new AudioScene(1, Guid.NewGuid(), "Original");
        var incoming = new AudioScene(1, Guid.NewGuid(), "Incoming");
        var store = new MutationRecordingStore([original]);
        string import = Path.Combine(directory, "atomic-import.json");
        Directory.CreateDirectory(directory);
        await new JsonSceneStore(import).SaveAsync([incoming]);
        var transfer = new SceneTransferService(store);
        SceneImportPreview preview = await transfer.PreviewImportAsync(import);

        await transfer.ApplyImportAsync(preview, SceneConflictChoice.Merge);

        Assert.Equal(1, store.MutationCount);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(2, (await store.LoadAsync()).Count);
    }

    [Fact]
    public async Task OpenImportStreamIsBoundedEvenWhenReportedLengthIsSmall()
    {
        await using var stream = new MisreportedLengthStream(new byte[SceneTransferService.MaximumImportBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await SceneTransferService.ReadDocumentAsync(stream));
    }

    private sealed class MisreportedLengthStream(byte[] content) : MemoryStream(content)
    {
        public override long Length => 1;
    }

    private sealed class MutationRecordingStore(IReadOnlyList<AudioScene> initial) : AudioDock.Core.Abstractions.ISceneStore
    {
        private IReadOnlyList<AudioScene> values = initial;
        public int MutationCount { get; private set; }
        public int SaveCount { get; private set; }
        public ValueTask<IReadOnlyList<AudioScene>> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(values);
        public ValueTask SaveAsync(IReadOnlyCollection<AudioScene> scenes, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            values = scenes.ToArray();
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<AudioScene>> MutateAsync(Func<IReadOnlyList<AudioScene>, IReadOnlyCollection<AudioScene>> mutation,
            CancellationToken cancellationToken = default)
        {
            MutationCount++;
            values = mutation(values).ToArray();
            return ValueTask.FromResult(values);
        }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
