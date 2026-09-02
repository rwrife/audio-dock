using AudioDock.Core.Models;
using AudioDock.Core.Persistence;

namespace AudioDock.Core.Tests.Persistence;

public sealed class JsonActivityStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"AudioDockActivity-{Guid.NewGuid():N}");

    [Fact]
    public async Task ConcurrentAppendsRetainEveryRecordWithinCapacity()
    {
        Directory.CreateDirectory(directory);
        var store = new JsonActivityStore(Path.Combine(directory, "activity.json"), capacity: 100);
        ActivityRecord[] records = Enumerable.Range(0, 32)
            .Select(index => new ActivityRecord(Guid.NewGuid(), null, $"Scene {index}",
                DateTimeOffset.UtcNow.AddMilliseconds(index), $"Record {index}"))
            .ToArray();

        await Task.WhenAll(records.Select(record => store.AppendAsync(record).AsTask()));

        IReadOnlyList<ActivityRecord> saved = await store.LoadAsync();
        Assert.Equal(records.Length, saved.Count);
        Assert.Equal(records.Select(record => record.Id).Order(), saved.Select(record => record.Id).Order());
    }

    [Fact]
    public async Task ClearQueuedAfterConcurrentAppendsLeavesNoActivity()
    {
        Directory.CreateDirectory(directory);
        var store = new JsonActivityStore(Path.Combine(directory, "activity.json"), capacity: 100);
        ActivityRecord[] records = Enumerable.Range(0, 32)
            .Select(index => new ActivityRecord(Guid.NewGuid(), null, $"Scene {index}", DateTimeOffset.UtcNow, $"Record {index}"))
            .ToArray();
        Task[] appends = records.Select(record => store.AppendAsync(record).AsTask()).ToArray();

        Task clear = store.ClearAsync().AsTask();
        await Task.WhenAll(appends.Append(clear));

        Assert.Empty(await store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
