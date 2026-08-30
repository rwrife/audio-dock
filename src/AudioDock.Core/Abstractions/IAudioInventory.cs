using AudioDock.Core.Models;

namespace AudioDock.Core.Abstractions;

public interface IAudioInventory
{
    AdapterCapabilities Capabilities { get; }

    ValueTask<AudioSnapshot> CaptureAsync(
        AudioInventoryOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AudioSnapshot> ObserveAsync(
        AudioInventoryOptions? options = null,
        CancellationToken cancellationToken = default);
}
