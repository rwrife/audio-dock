using AudioDock.Core.Models;
using AudioDock.Windows;
using AudioDock.Windows.Interop;

namespace AudioDock.Windows.Tests;

public sealed class WindowsAudioInventoryTests
{
    [Fact]
    public async Task CapturePreservesStableIdsForDuplicateAndUnpluggedEndpoints()
    {
        using var backend = new FakeBackend
        {
            Endpoints =
            [
                Endpoint("one", "USB Audio", EndpointState.Active),
                Endpoint("two", "USB Audio", EndpointState.Unplugged),
            ],
        };
        using var inventory = new WindowsAudioInventory(backend);

        AudioSnapshot snapshot = await inventory.CaptureAsync();

        Assert.Equal(["one", "two"], snapshot.Endpoints.Select(endpoint => endpoint.StableId));
        Assert.Equal(EndpointState.Unplugged, snapshot.Endpoints[1].State);
    }

    [Fact]
    public async Task CaptureRedactsPathsUnlessExplicitlyIncludedAndReportsInaccessibleProcess()
    {
        using var backend = new FakeBackend
        {
            Sessions = [Session("session", "Player", "C:\\Users\\person\\player.exe")],
            Diagnostics = [new("process", "session", "Process identity was inaccessible.", 5)],
        };
        using var inventory = new WindowsAudioInventory(backend);

        AudioSnapshot redacted = await inventory.CaptureAsync();
        AudioSnapshot included = await inventory.CaptureAsync(new(IncludeExecutablePaths: true));

        Assert.Null(redacted.Sessions[0].ExecutablePath);
        Assert.Equal("C:\\Users\\person\\player.exe", included.Sessions[0].ExecutablePath);
        Assert.Single(redacted.Diagnostics!);
    }

    [Fact]
    public async Task CaptureRepresentsEndpointFailureButSurfacesCatastrophicFailure()
    {
        using var partialBackend = new FakeBackend
        {
            Diagnostics = [new("endpoint", "broken", "COM call failed.", unchecked((int)0x80004005))],
        };
        using var partial = new WindowsAudioInventory(partialBackend);
        AudioSnapshot snapshot = await partial.CaptureAsync();
        Assert.Single(snapshot.Diagnostics!);

        using var catastrophicBackend = new FakeBackend { Failure = new InvalidOperationException("enumerator failed") };
        using var catastrophic = new WindowsAudioInventory(catastrophicBackend);
        await Assert.ThrowsAsync<AudioInventoryException>(async () => await catastrophic.CaptureAsync());
    }

    [Fact]
    public async Task ObserverDetectsDisappearingSessionAndDisposesBackend()
    {
        var backend = new FakeBackend { Sessions = [Session("session", "Player", null)] };
        var inventory = new WindowsAudioInventory(backend);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var snapshots = new List<AudioSnapshot>();

        await foreach (AudioSnapshot snapshot in inventory.ObserveAsync(
            new(PollInterval: TimeSpan.FromMilliseconds(10)), cancellation.Token))
        {
            snapshots.Add(snapshot);
            backend.Sessions = [];
            if (snapshots.Count == 2)
            {
                break;
            }
        }

        inventory.Dispose();
        Assert.Single(snapshots[0].Sessions);
        Assert.Empty(snapshots[1].Sessions);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task ObserverHonorsCancellationWithoutAnExtraCapture()
    {
        using var backend = new FakeBackend();
        using var inventory = new WindowsAudioInventory(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (AudioSnapshot _ in inventory.ObserveAsync(cancellationToken: cancellation.Token))
            {
            }
        });

        Assert.Equal(0, backend.CaptureCount);
    }

    [Fact]
    public void NativeSessionStateMappingReportsActiveSessionAsActive()
    {
        Assert.Equal(SessionState.Active, NativeAudioMapping.MapSessionState(AudioSessionState.Active));
        Assert.Equal(SessionState.Inactive, NativeAudioMapping.MapSessionState(AudioSessionState.Inactive));
        Assert.Equal(SessionState.Expired, NativeAudioMapping.MapSessionState(AudioSessionState.Expired));
        Assert.Equal("endpoint|session", NativeAudioMapping.ComposeSessionId("endpoint", "session"));
    }

    [Fact]
    public async Task DisposingInventoryCancelsActiveObserver()
    {
        var backend = new FakeBackend();
        var inventory = new WindowsAudioInventory(backend);
        await using IAsyncEnumerator<AudioSnapshot> observer = inventory.ObserveAsync(
            new(PollInterval: TimeSpan.FromMinutes(5))).GetAsyncEnumerator();

        Assert.True(await observer.MoveNextAsync());
        ValueTask<bool> pendingPoll = observer.MoveNextAsync();

        inventory.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pendingPoll.AsTask());
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task WriteReportsUnavailableCapabilityInsteadOfClaimingMutation()
    {
        using var backend = new FakeBackend();
        using var adapter = new WindowsAudioInventory(backend);

        ControlWriteResult result = await adapter.WriteAsync(
            new(ChangeKind.EndpointMute, "speakers", IsMuted: true));

        Assert.False(result.Succeeded);
        Assert.Contains("does not expose mutation", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, backend.CaptureCount);
    }

    [Fact]
    public async Task WriteDelegatesTypedCommandAndPreservesNativeFailure()
    {
        using var backend = new FakeControlBackend
        {
            Result = ControlWriteResult.Failure("Access denied.", 5),
        };
        using var adapter = new WindowsAudioInventory(backend);
        var command = new AudioControlCommand(
            ChangeKind.SessionVolume,
            "endpoint|session",
            Volume: new VolumeLevel(0.4));

        ControlWriteResult result = await adapter.WriteAsync(command);

        Assert.Same(command, backend.Command);
        Assert.False(result.Succeeded);
        Assert.Equal(5, result.NativeErrorCode);
        Assert.Contains("capability-probed", adapter.Capabilities.Limitation, StringComparison.Ordinal);
    }

    private static EndpointDescriptor Endpoint(string id, string name, EndpointState state) =>
        new(id, name, AudioDirection.Render, state, [], EndpointCapabilities.ReadOnly);

    private static SessionDescriptor Session(string id, string name, string? path) =>
        new(id, name, SessionState.Active, SessionCapabilities.ReadOnly, ExecutablePath: path);

    private sealed class FakeBackend : IWindowsAudioInventoryBackend
    {
        public IReadOnlyList<EndpointDescriptor> Endpoints { get; set; } = [];
        public IReadOnlyList<SessionDescriptor> Sessions { get; set; } = [];
        public IReadOnlyList<InventoryDiagnostic> Diagnostics { get; set; } = [];
        public Exception? Failure { get; init; }
        public bool Disposed { get; private set; }
        public int CaptureCount { get; private set; }

        public NativeInventorySnapshot Capture(bool includeExecutablePaths, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            if (Failure is not null) throw Failure;
            return new(Endpoints, Sessions, Diagnostics);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeControlBackend : IWindowsAudioControlBackend
    {
        public required ControlWriteResult Result { get; init; }

        public AudioControlCommand? Command { get; private set; }

        public NativeInventorySnapshot Capture(bool includeExecutablePaths, CancellationToken cancellationToken) =>
            new([], [], []);

        public ControlWriteResult Write(AudioControlCommand command, CancellationToken cancellationToken)
        {
            Command = command;
            return Result;
        }

        public void Dispose()
        {
        }
    }
}
