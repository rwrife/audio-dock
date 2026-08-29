using AudioDock.Core.Models;
using AudioDock.Core.Tests.Fakes;

namespace AudioDock.Core.Tests.Abstractions;

public sealed class AudioInventoryContractTests
{
    [Fact]
    public async Task FakeAdapterReturnsNormalizedSnapshotWithoutNativeObjects()
    {
        AudioSnapshot expected = new(
            DateTimeOffset.UnixEpoch,
            [TestData.Endpoint("speakers")],
            [TestData.Session("music")]);
        var inventory = new FakeAudioInventory(expected);

        AudioSnapshot actual = await inventory.CaptureAsync();

        Assert.Same(expected, actual);
        Assert.Equal(1, inventory.CaptureCount);
        Assert.True(inventory.Capabilities.CanInventoryEndpoints);
    }

    [Fact]
    public async Task FakeAdapterHonorsCancellationBeforeCapture()
    {
        var inventory = new FakeAudioInventory(AudioSnapshot.Empty);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await inventory.CaptureAsync(cancellation.Token));
        Assert.Equal(0, inventory.CaptureCount);
    }
}
