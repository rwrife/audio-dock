namespace AudioDock.Core.Models;

public sealed record EndpointDescriptor(
    string StableId,
    string FriendlyName,
    AudioDirection Direction,
    EndpointState State,
    IReadOnlyCollection<AudioRole> DefaultRoles,
    EndpointCapabilities Capabilities,
    VolumeLevel? Volume = null,
    bool? IsMuted = null,
    string? InterfaceId = null,
    string? ContainerId = null,
    string? Manufacturer = null,
    string? Product = null,
    IReadOnlyCollection<string>? Aliases = null);

public sealed record SessionDescriptor(
    string SessionId,
    string ProcessName,
    SessionState State,
    SessionCapabilities Capabilities,
    VolumeLevel? Volume = null,
    bool? IsMuted = null,
    string? PackageFamilyName = null,
    string? Publisher = null,
    string? ProductName = null,
    string? ExecutablePath = null,
    int? ProcessId = null,
    IReadOnlyCollection<string>? Aliases = null);

public sealed record AudioSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<EndpointDescriptor> Endpoints,
    IReadOnlyList<SessionDescriptor> Sessions)
{
    public static AudioSnapshot Empty { get; } = new(DateTimeOffset.UnixEpoch, [], []);
}
