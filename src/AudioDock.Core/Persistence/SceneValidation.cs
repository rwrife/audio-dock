using AudioDock.Core.Models;

namespace AudioDock.Core.Persistence;

public static class SceneValidation
{
    public const int MaximumScenes = 500;
    public const int MaximumRulesPerScene = 2000;
    public const int MaximumStringLength = 512;

    public static void Validate(IReadOnlyCollection<AudioScene> scenes)
    {
        if (scenes.Count > MaximumScenes) throw new InvalidDataException($"A scene file may contain at most {MaximumScenes} scenes.");
        var ids = new HashSet<Guid>();
        foreach (AudioScene scene in scenes)
        {
            if (scene.SchemaVersion != AudioScene.CurrentSchemaVersion) throw new UnsupportedSchemaVersionException(scene.SchemaVersion, AudioScene.CurrentSchemaVersion);
            if (!ids.Add(scene.Id)) throw new InvalidDataException($"Duplicate scene ID '{scene.Id}' is not allowed.");
            Text(scene.Name, "scene name", required: true);
            int rules = scene.RoleTargets.Count + scene.EndpointRules.Count + scene.ApplicationRules.Count;
            if (rules > MaximumRulesPerScene) throw new InvalidDataException($"Scene '{scene.Name}' exceeds the {MaximumRulesPerScene} rule limit.");
            foreach (RoleTarget target in scene.RoleTargets) { EnumValue(target.Role, "audio role"); Endpoint(target.Endpoint); }
            foreach (EndpointRule rule in scene.EndpointRules) { Endpoint(rule.Match); Controls(rule.Volume, rule.IsMuted); }
            foreach (ApplicationRule rule in scene.ApplicationRules) { Application(rule.Match); Controls(rule.Volume, rule.IsMuted); }
        }
    }

    private static void Endpoint(EndpointMatchRule rule)
    {
        EnumValue(rule.Direction, "audio direction");
        string?[] hints = [rule.ExactId, rule.UserAlias, rule.InterfaceId, rule.ContainerId, rule.FriendlyName, rule.Manufacturer, rule.Product];
        foreach (string? hint in hints) Text(hint, "endpoint match hint");
        if (hints.All(string.IsNullOrWhiteSpace)) throw new InvalidDataException("An endpoint match rule must contain at least one match hint.");
    }

    private static void Application(ApplicationMatchRule rule)
    {
        string?[] hints = [rule.PackageFamilyName, rule.ExecutablePath, rule.UserAlias, rule.Publisher, rule.ProductName, rule.ProcessName];
        foreach (string? hint in hints) Text(hint, "application match hint");
        if (hints.All(string.IsNullOrWhiteSpace)) throw new InvalidDataException("An application match rule must contain at least one match hint.");
        if (rule.ExecutablePath is { } path && !Path.IsPathFullyQualified(path) &&
            !(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')))
            throw new InvalidDataException("Executable-path match rules must use a fully qualified path.");
    }

    private static void Controls(VolumeLevel? volume, bool? muted)
    {
        if (volume is null && muted is null) throw new InvalidDataException("A control rule must set volume, mute, or both.");
    }

    private static void Text(string? value, string label, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"A {label} is required.");
        if (value?.Length > MaximumStringLength) throw new InvalidDataException($"A {label} exceeds {MaximumStringLength} characters.");
    }

    private static void EnumValue<T>(T value, string label) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new InvalidDataException($"Unknown {label} value '{value}'.");
    }
}
