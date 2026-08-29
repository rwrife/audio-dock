namespace AudioDock.Core.Models;

public sealed record EndpointCapabilities(
    bool CanSetDefaultRole,
    bool CanSetVolume,
    bool CanSetMute)
{
    public static EndpointCapabilities ReadOnly { get; } = new(false, false, false);
}

public sealed record SessionCapabilities(
    bool CanSetVolume,
    bool CanSetMute)
{
    public static SessionCapabilities ReadOnly { get; } = new(false, false);
}

public sealed record AdapterCapabilities(
    bool CanInventoryEndpoints,
    bool CanInventorySessions,
    bool CanObserveChanges,
    string? Limitation);
