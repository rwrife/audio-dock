using AudioDock.Core.Models;

namespace AudioDock.Core.Tests;

internal static class TestData
{
    internal static EndpointDescriptor Endpoint(
        string id,
        string name = "Speakers",
        AudioDirection direction = AudioDirection.Render,
        EndpointState state = EndpointState.Active,
        VolumeLevel? volume = null,
        bool? muted = false,
        EndpointCapabilities? capabilities = null,
        IReadOnlyCollection<AudioRole>? roles = null,
        string? interfaceId = null,
        string? containerId = null,
        string? manufacturer = null,
        string? product = null,
        IReadOnlyCollection<string>? aliases = null) =>
        new(
            id,
            name,
            direction,
            state,
            roles ?? [],
            capabilities ?? new(true, true, true),
            volume,
            muted,
            interfaceId,
            containerId,
            manufacturer,
            product,
            aliases);

    internal static SessionDescriptor Session(
        string id,
        string processName = "meeting",
        SessionState state = SessionState.Active,
        VolumeLevel? volume = null,
        bool? muted = false,
        SessionCapabilities? capabilities = null,
        string? packageFamily = null,
        string? publisher = null,
        string? product = null,
        string? path = null,
        IReadOnlyCollection<string>? aliases = null) =>
        new(
            id,
            processName,
            state,
            capabilities ?? new(true, true),
            volume,
            muted,
            packageFamily,
            publisher,
            product,
            path,
            42,
            aliases);
}
