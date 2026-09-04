using AudioDock.Core.Models;

namespace AudioDock.Windows.Tests;

/// <summary>
/// Safe simulated coverage; it neither creates the production COM backend nor writes host audio state.
/// </summary>
public sealed class WindowsCompatibilityHarnessTests
{
    [Fact]
    public async Task ReadOnlyHarnessRecordsRemovalReconnectAndFriendlyNameCollisionWithoutWriting()
    {
        var backend = new ScriptedReadOnlyBackend(
            [
                [
                    Endpoint("usb-a", "USB Audio", EndpointState.Active),
                    Endpoint("usb-b", "USB Audio", EndpointState.Unplugged),
                ],
                [
                    Endpoint("usb-a", "USB Audio", EndpointState.NotPresent),
                    Endpoint("usb-b", "USB Audio", EndpointState.Active),
                ],
            ]);
        using var inventory = new WindowsAudioInventory(backend);

        AudioSnapshot connected = await inventory.CaptureAsync();
        AudioSnapshot reconnected = await inventory.CaptureAsync();

        Assert.Equal(["usb-a", "usb-b"], connected.Endpoints.Select(endpoint => endpoint.StableId));
        Assert.Equal(EndpointState.NotPresent,
            reconnected.Endpoints.Single(endpoint => endpoint.StableId == "usb-a").State);
        Assert.Equal(EndpointState.Active,
            reconnected.Endpoints.Single(endpoint => endpoint.StableId == "usb-b").State);
        Assert.Equal(2, backend.CaptureCount);
    }

    [Fact]
    public async Task ReadOnlyHarnessReportsUnavailableAndInaccessibleSessionsWithoutWriting()
    {
        var backend = new ScriptedReadOnlyBackend(
            [[]],
            [new("process", "protected-session", "Process identity was inaccessible.", 5)]);
        using var inventory = new WindowsAudioInventory(backend);

        AudioSnapshot snapshot = await inventory.CaptureAsync();
        ControlWriteResult result = await inventory.WriteAsync(
            new(ChangeKind.SessionMute, "protected-session", IsMuted: true));

        Assert.Contains(snapshot.Diagnostics!, item => item.NativeErrorCode == 5);
        Assert.False(result.Succeeded);
        Assert.Contains("does not expose mutation", result.Detail, StringComparison.Ordinal);
    }

    private static EndpointDescriptor Endpoint(string id, string name, EndpointState state) =>
        new(id, name, AudioDirection.Render, state, [], EndpointCapabilities.ReadOnly);

    private sealed class ScriptedReadOnlyBackend(
        IReadOnlyList<IReadOnlyList<EndpointDescriptor>> captures,
        IReadOnlyList<InventoryDiagnostic>? diagnostics = null) : IWindowsAudioInventoryBackend
    {
        private int nextCapture;
        public int CaptureCount { get; private set; }

        public NativeInventorySnapshot Capture(bool includeExecutablePaths, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            int index = Math.Min(nextCapture++, captures.Count - 1);
            return new(captures[index], [], diagnostics ?? []);
        }

        public void Dispose() { }
    }
}
