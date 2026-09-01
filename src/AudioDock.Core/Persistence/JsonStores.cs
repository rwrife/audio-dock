using System.Text.Json;
using System.Text.Json.Serialization;
using AudioDock.Core.Abstractions;
using AudioDock.Core.Models;

namespace AudioDock.Core.Persistence;

public sealed class JsonSceneStore(string path) : ISceneStore
{
    public ValueTask<IReadOnlyList<AudioScene>> LoadAsync(CancellationToken cancellationToken = default) =>
        JsonFile.ReadAsync<AudioScene>(path, cancellationToken);

    public ValueTask SaveAsync(IReadOnlyCollection<AudioScene> scenes, CancellationToken cancellationToken = default) =>
        JsonFile.WriteAsync(path, scenes, cancellationToken);
}

public sealed class JsonActivityStore(string path, int capacity = 100) : IActivityStore
{
    public ValueTask<IReadOnlyList<ActivityRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
        JsonFile.ReadAsync<ActivityRecord>(path, cancellationToken);

    public async ValueTask AppendAsync(ActivityRecord activity, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ActivityRecord> existing = await LoadAsync(cancellationToken).ConfigureAwait(false);
        ActivityRecord[] bounded = existing.Append(activity).OrderByDescending(item => item.OccurredAt).Take(capacity).ToArray();
        await JsonFile.WriteAsync(path, bounded, cancellationToken).ConfigureAwait(false);
    }
}

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async ValueTask<IReadOnlyList<T>> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T[]>(stream, Options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    internal static async ValueTask WriteAsync<T>(string path, IEnumerable<T> values, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, values, Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, true);
    }
}
