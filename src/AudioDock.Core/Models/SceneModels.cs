namespace AudioDock.Core.Models;

public sealed record EndpointMatchRule(
    AudioDirection Direction,
    string? ExactId = null,
    string? UserAlias = null,
    string? InterfaceId = null,
    string? ContainerId = null,
    string? FriendlyName = null,
    string? Manufacturer = null,
    string? Product = null);

public sealed record ApplicationMatchRule(
    string? PackageFamilyName = null,
    string? ExecutablePath = null,
    string? UserAlias = null,
    string? Publisher = null,
    string? ProductName = null,
    string? ProcessName = null);

public sealed record RoleTarget(
    AudioRole Role,
    EndpointMatchRule Endpoint);

public sealed record EndpointRule(
    EndpointMatchRule Match,
    VolumeLevel? Volume = null,
    bool? IsMuted = null);

public sealed record ApplicationRule(
    ApplicationMatchRule Match,
    VolumeLevel? Volume = null,
    bool? IsMuted = null);

public sealed record AudioScene
{
    public const int CurrentSchemaVersion = 1;

    public AudioScene(
        int schemaVersion,
        Guid id,
        string name,
        IEnumerable<RoleTarget>? roleTargets = null,
        IEnumerable<EndpointRule>? endpointRules = null,
        IEnumerable<ApplicationRule>? applicationRules = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A scene must have a non-empty identifier.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A scene must have a name.", nameof(name));
        }

        SchemaVersion = schemaVersion;
        Id = id;
        Name = name.Trim();
        RoleTargets = (roleTargets ?? []).ToArray();
        EndpointRules = (endpointRules ?? []).ToArray();
        ApplicationRules = (applicationRules ?? []).ToArray();
    }

    [System.Text.Json.Serialization.JsonConstructor]
    public AudioScene(int schemaVersion, Guid id, string name, IReadOnlyList<RoleTarget> roleTargets,
        IReadOnlyList<EndpointRule> endpointRules, IReadOnlyList<ApplicationRule> applicationRules)
        : this(schemaVersion, id, name, roleTargets.AsEnumerable(), endpointRules.AsEnumerable(), applicationRules.AsEnumerable())
    {
    }

    public int SchemaVersion { get; }

    public Guid Id { get; }

    public string Name { get; }

    public IReadOnlyList<RoleTarget> RoleTargets { get; }

    public IReadOnlyList<EndpointRule> EndpointRules { get; }

    public IReadOnlyList<ApplicationRule> ApplicationRules { get; }
}
