using AudioDock.Core.Models;

namespace AudioDock.Core.Workflow;

public static class SceneRuleEditor
{
    private const int MaxMatchTextLength = 512;

    public static AudioScene UpsertRole(AudioScene scene, int index, AudioRole role, AudioDirection direction, string endpointId) =>
        Copy(scene, roles: Upsert(scene.RoleTargets, index, new RoleTarget(role, new(direction, ExactId: MatchText(endpointId, "endpoint ID")))));

    public static AudioScene RemoveRole(AudioScene scene, int index) => Copy(scene, roles: Remove(scene.RoleTargets, index));

    public static AudioScene UpsertEndpoint(AudioScene scene, int index, AudioDirection direction, string endpointId, string volume, bool? mute) =>
        Copy(scene, endpoints: Upsert(scene.EndpointRules, index, new EndpointRule(new(direction, ExactId: MatchText(endpointId, "endpoint ID")), Volume(volume), mute)));

    public static AudioScene RemoveEndpoint(AudioScene scene, int index) => Copy(scene, endpoints: Remove(scene.EndpointRules, index));

    public static AudioScene UpsertApplication(AudioScene scene, int index, string processName, string volume, bool? mute) =>
        Copy(scene, applications: Upsert(scene.ApplicationRules, index, new ApplicationRule(new(ProcessName: MatchText(processName, "process name")), Volume(volume), mute)));

    public static AudioScene RemoveApplication(AudioScene scene, int index) => Copy(scene, applications: Remove(scene.ApplicationRules, index));

    private static string MatchText(string value, string label)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxMatchTextLength) throw new ArgumentException($"The {label} must contain 1 through {MaxMatchTextLength} characters.");
        return trimmed;
    }

    private static VolumeLevel? Volume(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) || parsed is < 0 or > 1)
            throw new ArgumentException("Volume must be blank or a number from 0 through 1.");
        return new(parsed);
    }

    private static List<T> Upsert<T>(IReadOnlyList<T> source, int index, T value)
    {
        if (index < -1 || index >= source.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var result = source.ToList();
        if (index == -1) result.Add(value); else result[index] = value;
        return result;
    }

    private static List<T> Remove<T>(IReadOnlyList<T> source, int index)
    {
        if (index < 0 || index >= source.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var result = source.ToList(); result.RemoveAt(index); return result;
    }

    private static AudioScene Copy(AudioScene scene, IReadOnlyList<RoleTarget>? roles = null, IReadOnlyList<EndpointRule>? endpoints = null, IReadOnlyList<ApplicationRule>? applications = null) =>
        new(scene.SchemaVersion, scene.Id, scene.Name, roles ?? scene.RoleTargets, endpoints ?? scene.EndpointRules, applications ?? scene.ApplicationRules);
}
