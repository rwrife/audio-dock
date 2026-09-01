using AudioDock.Core.Models;

namespace AudioDock.Core.Abstractions;

public interface ISceneStore
{
    ValueTask<IReadOnlyList<AudioScene>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(IReadOnlyCollection<AudioScene> scenes, CancellationToken cancellationToken = default);
}

public interface IActivityStore
{
    ValueTask<IReadOnlyList<ActivityRecord>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask AppendAsync(ActivityRecord activity, CancellationToken cancellationToken = default);
}
