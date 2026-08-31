using AudioDock.Core.Models;

namespace AudioDock.Core.Abstractions;

public interface IAudioControlAdapter : IAudioInventory
{
    ValueTask<ControlWriteResult> WriteAsync(
        AudioControlCommand command,
        CancellationToken cancellationToken = default);
}
