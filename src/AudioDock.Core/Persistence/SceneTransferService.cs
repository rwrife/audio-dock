using System.Security.Cryptography;
using System.Text.Json;
using AudioDock.Core.Abstractions;
using AudioDock.Core.Models;

namespace AudioDock.Core.Persistence;

public enum SceneConflictChoice { Merge, Replace }
public sealed record SceneConflict(Guid Id, string ExistingName, string IncomingName);
public sealed record SceneImportPreview(IReadOnlyList<AudioScene> Incoming, IReadOnlyList<SceneConflict> Conflicts,
    bool ContainsExecutablePaths, string PortabilityWarning, string ExistingStoreRevision);
public sealed class StaleSceneImportPreviewException() : InvalidOperationException("Stored scenes changed after preview. Create a fresh import preview before applying.");
public sealed record SceneExportOptions(bool RedactExecutablePaths = true);
public sealed record SceneExportResult(int SceneCount, bool PathsRedacted, string PortabilityWarning);

public sealed class SceneTransferService(ISceneStore store, IDiagnosticSink? diagnostics = null)
{
    public const long MaximumImportBytes = 4 * 1024 * 1024;

    public async ValueTask<SceneImportPreview> PreviewImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        FileInfo info = new(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("The scene import file was not found.", sourcePath);
        if (info.Length > MaximumImportBytes) throw new InvalidDataException($"Imports may not exceed {MaximumImportBytes} bytes.");
        IReadOnlyList<AudioScene> incoming = await ReadDocumentAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AudioScene> existing = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, AudioScene> current = existing.ToDictionary(scene => scene.Id);
        SceneConflict[] conflicts = incoming.Where(scene => current.ContainsKey(scene.Id))
            .Select(scene => new SceneConflict(scene.Id, current[scene.Id].Name, scene.Name)).OrderBy(item => item.Id).ToArray();
        bool paths = incoming.SelectMany(scene => scene.ApplicationRules).Any(rule => rule.Match.ExecutablePath is not null);
        var preview = new SceneImportPreview(incoming.OrderBy(scene => scene.Id).ToArray(), conflicts, paths, paths
            ? "This file contains machine-specific executable paths. Review portability and privacy before continuing."
            : "Portable metadata cannot guarantee matches on another computer; preview targets before applying.", Revision(existing));
        await diagnostics.TryAppendAsync("scene_import_preview", $"Validated {incoming.Count} scene records with {conflicts.Length} conflicts.");
        return preview;
    }

    public async ValueTask ApplyImportAsync(SceneImportPreview preview, SceneConflictChoice choice, CancellationToken cancellationToken = default)
    {
        SceneValidation.Validate(preview.Incoming);
        IReadOnlyList<AudioScene> result = await store.MutateAsync(existing =>
        {
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(preview.ExistingStoreRevision), Convert.FromHexString(Revision(existing))))
                throw new StaleSceneImportPreviewException();
            if (choice == SceneConflictChoice.Replace) return preview.Incoming;
            Dictionary<Guid, AudioScene> merged = existing.ToDictionary(scene => scene.Id);
            foreach (AudioScene scene in preview.Incoming) merged[scene.Id] = scene;
            return merged.Values.OrderBy(scene => scene.Id).ToArray();
        }, cancellationToken).ConfigureAwait(false);
        await diagnostics.TryAppendAsync("scene_import_apply", $"Stored {result.Count} scene records using {choice.ToString().ToLowerInvariant()} conflict handling.");
    }

    public async ValueTask<SceneExportResult> ExportAsync(string destinationPath, SceneExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        IReadOnlyList<AudioScene> scenes = (await store.LoadAsync(cancellationToken).ConfigureAwait(false)).OrderBy(scene => scene.Id).ToArray();
        bool hadPaths = scenes.SelectMany(scene => scene.ApplicationRules).Any(rule => rule.Match.ExecutablePath is not null);
        if (options.RedactExecutablePaths) scenes = scenes.Select(Redact).ToArray();
        await JsonFile.WriteAsync(destinationPath, new SceneDocument(SceneDocument.CurrentSchemaVersion, scenes), cancellationToken).ConfigureAwait(false);
        await diagnostics.TryAppendAsync(options.RedactExecutablePaths ? "scene_export" : "scene_backup",
            $"Wrote {scenes.Count} scene records; executable paths redacted: {options.RedactExecutablePaths && hadPaths}.");
        return new(scenes.Count, options.RedactExecutablePaths && hadPaths, options.RedactExecutablePaths
            ? "Executable paths were redacted. Rules relying only on a path may need to be repaired after import."
            : "Executable paths are machine-specific and may disclose user or installation details.");
    }

    public ValueTask<SceneExportResult> BackupAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        ExportAsync(destinationPath, new SceneExportOptions(false), cancellationToken);

    public ValueTask<SceneImportPreview> PreviewRestoreAsync(string backupPath, CancellationToken cancellationToken = default) =>
        PreviewImportAsync(backupPath, cancellationToken);

    private static async ValueTask<IReadOnlyList<AudioScene>> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<AudioScene>> ReadDocumentAsync(Stream source, CancellationToken cancellationToken = default)
    {
        await using var bounded = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (bounded.Length <= MaximumImportBytes)
        {
            int remaining = (int)Math.Min(buffer.Length, MaximumImportBytes + 1 - bounded.Length);
            int read = await source.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (bounded.Length > MaximumImportBytes) throw new InvalidDataException($"Imports may not exceed {MaximumImportBytes} bytes.");
        bounded.Position = 0;
        JsonDocument parsed;
        try { parsed = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException exception) { throw new InvalidDataException("The import is not valid JSON.", exception); }
        using JsonDocument json = parsed;
        JsonElement root = json.RootElement;
        AudioScene[] scenes;
        if (root.ValueKind == JsonValueKind.Array) scenes = root.Deserialize<AudioScene[]>(JsonFile.Options) ?? [];
        else
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out JsonElement version) || !version.TryGetInt32(out int schema))
                throw new InvalidDataException("The import has no supported schema version.");
            if (schema != SceneDocument.CurrentSchemaVersion) throw new UnsupportedSchemaVersionException(schema, SceneDocument.CurrentSchemaVersion);
            scenes = (root.Deserialize<SceneDocument>(JsonFile.Options) ?? throw new InvalidDataException("The import document is empty.")).Scenes.ToArray();
        }
        SceneValidation.Validate(scenes);
        return scenes;
    }

    private static AudioScene Redact(AudioScene scene) => new(scene.SchemaVersion, scene.Id, scene.Name, scene.RoleTargets, scene.EndpointRules,
        scene.ApplicationRules.Select(rule => rule with { Match = rule.Match with { ExecutablePath = null } })
            .Where(rule => HasPortableHint(rule.Match)));

    private static bool HasPortableHint(ApplicationMatchRule rule) =>
        new[] { rule.PackageFamilyName, rule.UserAlias, rule.Publisher, rule.ProductName, rule.ProcessName }.Any(value => !string.IsNullOrWhiteSpace(value));

    private static string Revision(IReadOnlyList<AudioScene> scenes)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(scenes.OrderBy(scene => scene.Id), JsonFile.Options);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }
}
