using AudioDock.Core.Models;

namespace AudioDock.Core.Abstractions;

public interface IAudioInventory
{
    AdapterCapabilities Capabilities { get; }

    ValueTask<AudioSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}
