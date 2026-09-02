using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioDock.Core.Abstractions;
using AudioDock.Core.Models;

namespace AudioDock.Core.Persistence;

public sealed class JsonSceneStore(string path) : ISceneStore
{
    public async ValueTask<IReadOnlyList<AudioScene>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await JsonFile.CoordinateAsync(path, async normalized =>
        {
            await JsonFile.RecoverUnlockedAsync(normalized, IsCompleteSceneDocument, IsSupportedSceneDocument, cancellationToken).ConfigureAwait(false);
            return await LoadUnlockedAsync(normalized, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<AudioScene>> LoadUnlockedAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            AudioScene[] legacy = root.Deserialize<AudioScene[]>(JsonFile.Options) ?? [];
            SceneValidation.Validate(legacy);
            return legacy;
        }

        SceneDocument document = root.Deserialize<SceneDocument>(JsonFile.Options)
            ?? throw new InvalidDataException("The scene document is empty.");
        if (document.SchemaVersion != SceneDocument.CurrentSchemaVersion)
            throw new UnsupportedSchemaVersionException(document.SchemaVersion, SceneDocument.CurrentSchemaVersion);
        SceneValidation.Validate(document.Scenes);
        return document.Scenes;
    }

    public ValueTask SaveAsync(IReadOnlyCollection<AudioScene> scenes, CancellationToken cancellationToken = default)
    {
        SceneValidation.Validate(scenes);
        return JsonFile.WriteAsync(path, new SceneDocument(SceneDocument.CurrentSchemaVersion, scenes.OrderBy(scene => scene.Id).ToArray()), cancellationToken);
    }

    public ValueTask<IReadOnlyList<AudioScene>> MutateAsync(
        Func<IReadOnlyList<AudioScene>, IReadOnlyCollection<AudioScene>> mutation,
        CancellationToken cancellationToken = default) => JsonFile.CoordinateAsync(path, async normalized =>
    {
        await JsonFile.RecoverUnlockedAsync(normalized, IsCompleteSceneDocument, IsSupportedSceneDocument, cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<AudioScene> updated = mutation(await LoadUnlockedAsync(normalized, cancellationToken).ConfigureAwait(false));
        SceneValidation.Validate(updated);
        AudioScene[] ordered = updated.OrderBy(scene => scene.Id).ToArray();
        await JsonFile.WriteUnlockedAsync(normalized, new SceneDocument(SceneDocument.CurrentSchemaVersion, ordered), cancellationToken).ConfigureAwait(false);
        return (IReadOnlyList<AudioScene>)ordered;
    }, cancellationToken);

    private static bool IsCompleteSceneDocument(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return IsSupportedSceneDocument(root);
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("schemaVersion", out JsonElement version) && version.TryGetInt32(out int schema) &&
            root.TryGetProperty("scenes", out JsonElement scenes) && scenes.ValueKind == JsonValueKind.Array &&
            (schema != SceneDocument.CurrentSchemaVersion || IsSupportedSceneDocument(root));
    }

    private static bool IsSupportedSceneDocument(JsonElement root)
    {
        try
        {
            AudioScene[] values;
            if (root.ValueKind == JsonValueKind.Array) values = root.Deserialize<AudioScene[]>(JsonFile.Options) ?? [];
            else
            {
                if (!root.TryGetProperty("schemaVersion", out JsonElement version) || !version.TryGetInt32(out int schema) ||
                    schema != SceneDocument.CurrentSchemaVersion || !root.TryGetProperty("scenes", out JsonElement scenes) || scenes.ValueKind != JsonValueKind.Array) return false;
                values = (root.Deserialize<SceneDocument>(JsonFile.Options) ?? throw new JsonException()).Scenes.ToArray();
            }
            SceneValidation.Validate(values);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException) { return false; }
    }
}

public sealed record SceneDocument(int SchemaVersion, IReadOnlyList<AudioScene> Scenes)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class UnsupportedSchemaVersionException(int actual, int supported)
    : IOException($"Schema version {actual} is not supported; this version of Audio Dock supports {supported}.")
{
    public int ActualVersion { get; } = actual;
    public int SupportedVersion { get; } = supported;
}

public sealed class JsonActivityStore(string path, int capacity = 100) : IActivityStore
{
    public ValueTask<IReadOnlyList<ActivityRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
        JsonFile.ReadAsync<ActivityRecord>(path, cancellationToken);

    public async ValueTask AppendAsync(ActivityRecord activity, CancellationToken cancellationToken = default)
    {
        await JsonFile.CoordinateAsync(path, async normalized =>
        {
            IReadOnlyList<ActivityRecord> existing = await JsonFile.ReadUnlockedAsync<ActivityRecord>(normalized, cancellationToken).ConfigureAwait(false);
            ActivityRecord[] bounded = existing.Append(activity).OrderByDescending(item => item.OccurredAt).Take(capacity).ToArray();
            await JsonFile.WriteUnlockedAsync(normalized, bounded, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await JsonFile.CoordinateAsync(path, normalized =>
        {
            if (File.Exists(normalized)) File.Delete(normalized);
            if (File.Exists(normalized + ".tmp")) File.Delete(normalized + ".tmp");
            return ValueTask.FromResult(true);
        }, cancellationToken).ConfigureAwait(false);
    }
}

internal static class JsonFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static ValueTask<IReadOnlyList<T>> ReadAsync<T>(string path, CancellationToken cancellationToken) =>
        CoordinateAsync(path, normalized => ReadUnlockedAsync<T>(normalized, cancellationToken), cancellationToken);

    internal static async ValueTask<IReadOnlyList<T>> ReadUnlockedAsync<T>(string normalized, CancellationToken cancellationToken)
    {
        if (!File.Exists(normalized)) return [];
        await using FileStream stream = new(normalized, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T[]>(stream, Options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    internal static async ValueTask WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await CoordinateAsync(path, async normalized =>
        {
            await WriteUnlockedAsync(normalized, value, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<T> CoordinateAsync<T>(string path, Func<string, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        string normalized = Normalize(path);
        SemaphoreSlim gate = Gates.GetOrAdd(normalized, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action(normalized).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal static async ValueTask WriteUnlockedAsync<T>(string normalized, T value, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = normalized + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        Commit(temporary, normalized);
    }

    internal static async ValueTask WriteTextUnlockedAsync(string normalized, string content, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = normalized + ".tmp";
        await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        await using (FileStream stream = new(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stream.Flush(flushToDisk: true);
        Commit(temporary, normalized);
    }

    internal static async ValueTask RecoverAsync(string path, Func<JsonElement, bool> isComplete,
        Func<JsonElement, bool> accepts, CancellationToken cancellationToken)
    {
        await CoordinateAsync(path, async normalized =>
        {
            await RecoverUnlockedAsync(normalized, isComplete, accepts, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask RecoverUnlockedAsync(string normalized, Func<JsonElement, bool> isComplete,
        Func<JsonElement, bool> accepts, CancellationToken cancellationToken)
    {
        string temporary = normalized + ".tmp";
        if (!File.Exists(temporary)) return;
        if (await IsAcceptedAsync(normalized, isComplete, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(temporary); // A complete committed primary, including a future schema, always wins.
            return;
        }
        if (await IsAcceptedAsync(temporary, accepts, cancellationToken).ConfigureAwait(false))
            Commit(temporary, normalized);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static void Commit(string temporary, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporary, destination);
            return;
        }

        try { File.Replace(temporary, destination, null); }
        catch (PlatformNotSupportedException) { File.Move(temporary, destination, true); }
    }

    private static async ValueTask<bool> IsAcceptedAsync(string candidate, Func<JsonElement, bool> accepts, CancellationToken cancellationToken)
    {
        if (!File.Exists(candidate)) return false;
        try
        {
            await using FileStream stream = new(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return accepts(json.RootElement);
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }
}
