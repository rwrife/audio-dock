using System.Text.Json;
using System.Text.RegularExpressions;
using AudioDock.Core.Abstractions;

namespace AudioDock.Core.Persistence;

public static class AudioDockDataPaths
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioDock");
    public static string Scenes(string root) => Path.Combine(root, "scenes.json");
    public static string Settings(string root) => Path.Combine(root, "settings.json");
    public static string Activity(string root) => Path.Combine(root, "activity.json");
    public static string Diagnostics(string root) => Path.Combine(root, "diagnostics.jsonl");
}

public sealed record AppSettings(int SchemaVersion, bool HotkeyEnabled, int HotkeyModifierChoice, string HotkeyKey,
    bool AllowExecutablePathMatching = false)
{
    public const int CurrentSchemaVersion = 1;
    public static AppSettings Defaults => new(CurrentSchemaVersion, false, 0, "D", false);
}

public sealed class JsonSettingsStore(string path)
{
    public async ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await JsonFile.RecoverAsync(path, IsCompleteSettings, IsSupportedSettings, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path)) return AppSettings.Defaults;
        JsonDocument document;
        await using (FileStream stream = File.OpenRead(path))
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        using (document)
        {
            AppSettings value;
            if (TryGetProperty(document.RootElement, "schemaVersion", out _))
            {
                value = document.RootElement.Deserialize<AppSettings>(JsonFile.Options)
                    ?? throw new InvalidDataException("Settings are empty.");
                if (value.SchemaVersion != AppSettings.CurrentSchemaVersion)
                    throw new UnsupportedSchemaVersionException(value.SchemaVersion, AppSettings.CurrentSchemaVersion);
            }
            else
            {
                LegacyHotkeyPreferences legacy = document.RootElement.Deserialize<LegacyHotkeyPreferences>(JsonFile.Options)
                    ?? throw new InvalidDataException("Legacy settings are empty.");
                value = new(AppSettings.CurrentSchemaVersion, legacy.Enabled, legacy.ModifierChoice, legacy.Key, false);
                ValidateHotkey(value);
                await SaveAsync(value, cancellationToken).ConfigureAwait(false);
                return value;
            }
            ValidateHotkey(value);
            return value;
        }
    }

    public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion) throw new UnsupportedSchemaVersionException(settings.SchemaVersion, AppSettings.CurrentSchemaVersion);
        ValidateHotkey(settings);
        return JsonFile.WriteAsync(path, settings, cancellationToken);
    }

    private static bool IsCompleteSettings(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (TryGetProperty(root, "schemaVersion", out JsonElement version))
            return version.TryGetInt32(out _) && HasVersionedFields(root);
        try
        {
            LegacyHotkeyPreferences legacy = root.Deserialize<LegacyHotkeyPreferences>(JsonFile.Options)!;
            ValidateHotkey(new(AppSettings.CurrentSchemaVersion, legacy.Enabled, legacy.ModifierChoice, legacy.Key, false));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NullReferenceException) { return false; }
    }

    private static bool IsSupportedSettings(JsonElement root)
    {
        if (!IsCompleteSettings(root) || !TryGetProperty(root, "schemaVersion", out JsonElement version) ||
            version.GetInt32() != AppSettings.CurrentSchemaVersion) return false;
        try { ValidateHotkey(root.Deserialize<AppSettings>(JsonFile.Options)!); return true; }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NullReferenceException) { return false; }
    }

    private static bool HasVersionedFields(JsonElement root) =>
        TryGetProperty(root, "hotkeyEnabled", out JsonElement enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        TryGetProperty(root, "hotkeyModifierChoice", out JsonElement modifier) && modifier.TryGetInt32(out _) &&
        TryGetProperty(root, "hotkeyKey", out JsonElement key) && key.ValueKind == JsonValueKind.String;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed record LegacyHotkeyPreferences(bool Enabled, int ModifierChoice, string Key);

    private static void ValidateHotkey(AppSettings value)
    {
        if (value.HotkeyModifierChoice is < 0 or > 2 || value.HotkeyKey is null || value.HotkeyKey.Length != 1 || !char.IsAsciiLetter(value.HotkeyKey[0]))
            throw new InvalidDataException("Hotkey settings are invalid.");
    }
}

public sealed record DiagnosticEvent(DateTimeOffset OccurredAt, string Level, string Event, string Detail);

public interface IDiagnosticSink
{
    ValueTask AppendAsync(DiagnosticEvent item, CancellationToken cancellationToken = default);
}

internal static class DiagnosticSinkExtensions
{
    internal static async ValueTask TryAppendAsync(this IDiagnosticSink? sink, string eventName, string detail)
    {
        if (sink is null) return;
        try { await sink.AppendAsync(new(DateTimeOffset.UtcNow, "Info", eventName, detail), CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }
}

public sealed partial class LocalDiagnosticStore(string path, long maximumBytes = 512 * 1024, TimeSpan? maximumAge = null) : IDiagnosticSink
{
    private static readonly JsonSerializerOptions DiagnosticOptions = new(JsonFile.Options) { WriteIndented = false };
    private readonly TimeSpan age = maximumAge ?? TimeSpan.FromDays(14);

    public async ValueTask AppendAsync(DiagnosticEvent item, CancellationToken cancellationToken = default)
    {
        await JsonFile.CoordinateAsync(path, async normalized =>
        {
            List<DiagnosticEvent> retained = await ReadUnlockedAsync(normalized, cancellationToken).ConfigureAwait(false);
            retained.Add(item with { Detail = Redact(item.Detail) });
            await RewriteUnlockedAsync(normalized, retained, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DiagnosticEvent>> InspectAsync(CancellationToken cancellationToken = default)
    {
        return await JsonFile.CoordinateAsync(path, async normalized =>
            (IReadOnlyList<DiagnosticEvent>)(await ReadUnlockedAsync(normalized, cancellationToken).ConfigureAwait(false))
                .OrderByDescending(item => item.OccurredAt).ToArray(), cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<List<DiagnosticEvent>> ReadUnlockedAsync(string normalized, CancellationToken cancellationToken)
    {
        var result = new List<DiagnosticEvent>();
        if (!File.Exists(normalized)) return result;
        foreach (string line in await File.ReadAllLinesAsync(normalized, cancellationToken).ConfigureAwait(false))
        {
            try { if (JsonSerializer.Deserialize<DiagnosticEvent>(line, DiagnosticOptions) is { } item) result.Add(item); }
            catch (JsonException) { }
        }
        return result;
    }

    private async ValueTask RewriteUnlockedAsync(string normalized, IEnumerable<DiagnosticEvent> items, CancellationToken cancellationToken)
    {
        IReadOnlyList<DiagnosticEvent> retained = items
            .Where(item => item.OccurredAt >= DateTimeOffset.UtcNow - age).OrderByDescending(item => item.OccurredAt).ToArray();
        var lines = new List<string>(); long bytes = 0;
        foreach (DiagnosticEvent item in retained)
        {
            string line = JsonSerializer.Serialize(item, DiagnosticOptions);
            long length = System.Text.Encoding.UTF8.GetByteCount(line + Environment.NewLine);
            if (bytes + length > maximumBytes) break;
            lines.Add(line); bytes += length;
        }
        string content = string.Join(Environment.NewLine, lines.OrderBy(line => line));
        if (content.Length > 0) content += Environment.NewLine;
        await JsonFile.WriteTextUnlockedAsync(normalized, content, cancellationToken).ConfigureAwait(false);
    }

    private static string Redact(string value) => WindowsPath().Replace(UnixPath().Replace(value, "[path redacted]"), "[path redacted]");
    [GeneratedRegex(@"(?i)(?:\\\\\?\\[A-Z]:\\|\\\\[^\\\s]+\\[^\\\s]+\\|\b[A-Z]:\\)[^\r\n\t\""']+")]
    private static partial Regex WindowsPath();
    [GeneratedRegex(@"(?<!\w)/(?:[^/\s]+/)+[^\s,;]+")]
    private static partial Regex UnixPath();
}

public sealed class LocalDataMaintenance(string root, IDiagnosticSink? diagnostics = null, ISceneStore? sceneStore = null)
{
    private readonly ISceneStore scenes = sceneStore ?? new JsonSceneStore(AudioDockDataPaths.Scenes(root));
    public string DataLocation => Path.GetFullPath(root);
    public async ValueTask ClearScenesAsync() { await scenes.MutateAsync(_ => []); await diagnostics.TryAppendAsync("scenes_clear", "Stored scene data cleared."); }
    public async ValueTask ClearActivityAsync() { await new JsonActivityStore(AudioDockDataPaths.Activity(root)).ClearAsync(); await diagnostics.TryAppendAsync("activity_clear", "Stored activity history cleared."); }
    public ValueTask ClearDiagnosticsAsync() => new LocalDiagnosticStore(AudioDockDataPaths.Diagnostics(root)).ClearAsync();
    public async ValueTask ClearSettingsAsync() { await DeleteAsync(AudioDockDataPaths.Settings(root)); await diagnostics.TryAppendAsync("settings_clear", "Stored settings cleared."); }
    private static ValueTask DeleteAsync(string path) { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); return ValueTask.CompletedTask; }
}
