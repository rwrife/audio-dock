using AudioDock.Core.Abstractions;
using AudioDock.Core.Models;

namespace AudioDock.Core.Tests.Fakes;

internal sealed class FakeAudioInventory : IAudioInventory
{
    private AudioSnapshot snapshot;

    internal FakeAudioInventory(AudioSnapshot snapshot)
    {
        this.snapshot = snapshot;
    }

    public AdapterCapabilities Capabilities { get; } = new(true, true, true, null);

    public int CaptureCount { get; private set; }

    public ValueTask<AudioSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaptureCount++;
        return ValueTask.FromResult(snapshot);
    }

    internal void SetSnapshot(AudioSnapshot value) => snapshot = value;
}
