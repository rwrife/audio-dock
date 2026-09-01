using System.Runtime.CompilerServices;
using AudioDock.Core.Abstractions;
using AudioDock.Core.Execution;
using AudioDock.Core.Models;

namespace AudioDock.Core.Tests.Execution;

public sealed class SceneExecutorTests
{
    [Fact]
    public async Task ApplyCapturesPreStateBeforeMutationAndVerifiesEveryWrite()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2),
            muted: false);
        var adapter = new FakeControlAdapter([endpoint], []);
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7), true)]);
        var executor = new SceneExecutor(adapter);

        ApplyResult result = await executor.ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.Applied, result.State);
        Assert.Equal(
            ["capture", "capture", "write:EndpointVolume", "capture", "capture", "write:EndpointMute", "capture"],
            adapter.Events);
        Assert.Equal(
            [OperationResultState.Applied, OperationResultState.Applied],
            result.Operations.Select(operation => operation.State));
        Assert.Equal(RollbackState.Available, result.Rollback.State);
        Assert.NotNull(result.Rollback.SnapshotId);
    }

    [Fact]
    public async Task StateChangedAfterInitialValidationIsNotOverwritten()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speakers", volume: new VolumeLevel(0.2));
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            MutateOnCapture = (2, ChangeKind.EndpointVolume, "speakers", 0.4, null),
        };
        AudioScene scene = Scene(endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7))]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.Failed, result.State);
        Assert.Contains("Re-preview", Assert.Single(result.Operations).Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(adapter.Events, item => item.StartsWith("write", StringComparison.Ordinal));
        Assert.Equal(new VolumeLevel(0.4), (await adapter.CaptureAsync()).Endpoints[0].Volume);
    }

    [Fact]
    public async Task StaleLaterOperationStopsItsWriteAndRollsBackEarlierWrite()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speakers", volume: new VolumeLevel(0.2), muted: false);
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            MutateOnCapture = (4, ChangeKind.EndpointMute, "speakers", null, true),
        };
        AudioScene scene = Scene(endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7), true)]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.RolledBack, result.State);
        Assert.Equal([OperationResultState.RolledBack, OperationResultState.Failed], result.Operations.Select(item => item.State));
        Assert.Equal(2, adapter.Events.Count(item => item.StartsWith("write:EndpointVolume", StringComparison.Ordinal)));
        Assert.DoesNotContain("write:EndpointMute", adapter.Events);
        AudioSnapshot final = await adapter.CaptureAsync();
        Assert.Equal(new VolumeLevel(0.2), final.Endpoints[0].Volume);
        Assert.True(final.Endpoints[0].IsMuted);
    }

    [Fact]
    public async Task FailedWriteRollsBackEarlierChangesAndReportsBothFacts()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2),
            muted: false);
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            FailOnceKind = ChangeKind.EndpointMute,
        };
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7), true)],
            applications: [new(new(ProcessName: "later"), IsMuted: true)]);
        var executor = new SceneExecutor(adapter);

        ApplyResult result = await executor.ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.RolledBack, result.State);
        Assert.Equal(OperationResultState.RolledBack, result.Operations[0].State);
        Assert.Equal(OperationResultState.Failed, result.Operations[1].State);
        Assert.Equal(OperationResultState.Skipped, result.Operations[2].State);
        Assert.Contains("transaction stopped", result.Operations[2].Detail, StringComparison.Ordinal);
        Assert.Equal(RollbackState.Succeeded, result.Rollback.State);
        AudioSnapshot restored = await adapter.CaptureAsync();
        Assert.Equal(new VolumeLevel(0.2), restored.Endpoints[0].Volume);
        Assert.False(restored.Endpoints[0].IsMuted);
    }

    [Fact]
    public async Task UndoRestoresTheLatestPreApplySnapshotExactlyOnce()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2));
        var adapter = new FakeControlAdapter([endpoint], []);
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7))]);
        var executor = new SceneExecutor(adapter);
        ApplyResult applied = await executor.ApplyAsync(scene);

        RollbackFact undone = await executor.UndoAsync(applied.Rollback.SnapshotId!.Value);
        RollbackFact repeated = await executor.UndoAsync(applied.Rollback.SnapshotId.Value);

        Assert.Equal(RollbackState.Succeeded, undone.State);
        Assert.Equal(RollbackState.Cleared, repeated.State);
        AudioSnapshot restored = await adapter.CaptureAsync();
        Assert.Equal(new VolumeLevel(0.2), restored.Endpoints[0].Volume);
    }

    [Fact]
    public async Task CancellationStopsAtTheNextBoundaryAndRollsBackCompletedWrites()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2),
            muted: false);
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            CancelAfterFirstWrite = cancellation,
        };
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7), true)]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene, cancellation.Token);

        Assert.Equal(ApplyOverallState.Canceled, result.State);
        Assert.Equal(OperationResultState.RolledBack, result.Operations[0].State);
        Assert.Equal(OperationResultState.Skipped, result.Operations[1].State);
        Assert.Equal(RollbackState.Succeeded, result.Rollback.State);
    }

    [Fact]
    public async Task RollbackFailureIsVisibleOnTheOperationAndOverallResult()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2),
            muted: false);
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            FailOnceKind = ChangeKind.EndpointMute,
            FailAttempt = (ChangeKind.EndpointVolume, 2),
        };
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7), true)]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.Partial, result.State);
        Assert.Equal(OperationResultState.RollbackFailed, result.Operations[0].State);
        Assert.Equal(RollbackState.Failed, result.Rollback.State);
    }

    [Fact]
    public async Task SessionThatDisappearsImmediatelyBeforeUseIsSkipped()
    {
        SessionDescriptor session = TestData.Session(
            "session",
            processName: "meeting",
            volume: new VolumeLevel(0.2));
        var adapter = new FakeControlAdapter([], [session])
        {
            RemoveSessionsOnCapture = 2,
        };
        AudioScene scene = Scene(
            applications: [new(new(ProcessName: "meeting"), new VolumeLevel(0.7))]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        OperationResult operation = Assert.Single(result.Operations);
        Assert.Equal(OperationResultState.Skipped, operation.State);
        Assert.Contains("disappeared", operation.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(adapter.Events, item => item.StartsWith("write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObservableVerificationMismatchTriggersBestEffortRollback()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2));
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            IgnoreOnceKind = ChangeKind.EndpointVolume,
        };
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7))]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        OperationResult operation = Assert.Single(result.Operations);
        Assert.Equal(OperationResultState.VerificationMismatch, operation.State);
        Assert.Equal("0.2", operation.ObservedValue);
        Assert.Equal(RollbackState.Succeeded, result.Rollback.State);
    }

    [Fact]
    public async Task NonRunningApplicationRulesRemainDeferredDuringApply()
    {
        var adapter = new FakeControlAdapter([], []);
        AudioScene scene = Scene(
            applications: [new(new(ProcessName: "meeting"), new VolumeLevel(0.7), true)]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.Partial, result.State);
        Assert.All(result.Operations, operation => Assert.Equal(OperationResultState.Deferred, operation.State));
        Assert.Equal(RollbackState.NotRequired, result.Rollback.State);
    }

    [Fact]
    public async Task AppliesAndVerifiesRoleEndpointAndRunningSessionControls()
    {
        EndpointDescriptor oldDefault = TestData.Endpoint("old", roles: [AudioRole.Communications]);
        EndpointDescriptor target = TestData.Endpoint(
            "headset",
            volume: new VolumeLevel(0.2),
            muted: false);
        SessionDescriptor session = TestData.Session(
            "meeting-session",
            processName: "meeting",
            volume: new VolumeLevel(0.3),
            muted: false);
        var adapter = new FakeControlAdapter([oldDefault, target], [session]);
        AudioScene scene = Scene(
            roles: [new(AudioRole.Communications, new(AudioDirection.Render, ExactId: "headset"))],
            endpoints: [new(new(AudioDirection.Render, ExactId: "headset"), new VolumeLevel(0.7), true)],
            applications: [new(new(ProcessName: "meeting"), new VolumeLevel(0.8), true)]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.Applied, result.State);
        Assert.Equal(5, result.Operations.Count);
        Assert.All(result.Operations, operation => Assert.Equal(OperationResultState.Applied, operation.State));
    }

    [Fact]
    public async Task UnknownPreStatePreventsAnIrreversibleWrite()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speakers", volume: null);
        var adapter = new FakeControlAdapter([endpoint], []);
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7))]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        OperationResult operation = Assert.Single(result.Operations);
        Assert.Equal(OperationResultState.Skipped, operation.State);
        Assert.Contains("pre-apply state", operation.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(adapter.Events, item => item.StartsWith("write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdapterExceptionAfterAMutationBecomesAFailureAndRollsBack()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2),
            muted: false);
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            ThrowAttempt = (ChangeKind.EndpointMute, 1),
        };
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7), true)]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.RolledBack, result.State);
        Assert.Equal(OperationResultState.RolledBack, result.Operations[0].State);
        Assert.Equal(OperationResultState.Failed, result.Operations[1].State);
        Assert.Contains("adapter threw", result.Operations[1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadBackExceptionAfterMutationStillTriggersRollback()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speakers", volume: new VolumeLevel(0.2));
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            ThrowOnCapture = 3,
        };
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7))]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene);

        Assert.Equal(ApplyOverallState.RolledBack, result.State);
        Assert.Equal(OperationResultState.Failed, Assert.Single(result.Operations).State);
        Assert.Equal(RollbackState.Succeeded, result.Rollback.State);
    }

    [Fact]
    public async Task RollbackFailureTakesPrecedenceOverCanceledOverallState()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            volume: new VolumeLevel(0.2),
            muted: false);
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeControlAdapter([endpoint], [])
        {
            CancelAfterFirstWrite = cancellation,
            ThrowAttempt = (ChangeKind.EndpointVolume, 2),
        };
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.7), true)]);

        ApplyResult result = await new SceneExecutor(adapter).ApplyAsync(scene, cancellation.Token);

        Assert.Equal(ApplyOverallState.Partial, result.State);
        Assert.Equal(RollbackState.Failed, result.Rollback.State);
        Assert.Equal(OperationResultState.RollbackFailed, result.Operations[0].State);
        Assert.Contains("Canceled", result.Operations[1].Detail, StringComparison.Ordinal);
    }

    private static AudioScene Scene(
        IEnumerable<RoleTarget>? roles = null,
        IEnumerable<EndpointRule>? endpoints = null,
        IEnumerable<ApplicationRule>? applications = null) =>
        new(AudioScene.CurrentSchemaVersion, Guid.Parse("11111111-1111-1111-1111-111111111111"), "Test", roles, endpoints, applications);

    private sealed class FakeControlAdapter(
        IReadOnlyList<EndpointDescriptor> endpoints,
        IReadOnlyList<SessionDescriptor> sessions) : IAudioControlAdapter
    {
        private List<EndpointDescriptor> endpoints = [.. endpoints];
        private List<SessionDescriptor> sessions = [.. sessions];
        private readonly Dictionary<ChangeKind, int> attempts = [];
        private int captureCount;

        public List<string> Events { get; } = [];

        public ChangeKind? FailOnceKind { get; init; }

        public CancellationTokenSource? CancelAfterFirstWrite { get; init; }

        public (ChangeKind Kind, int Attempt)? FailAttempt { get; init; }

        public (ChangeKind Kind, int Attempt)? ThrowAttempt { get; init; }

        public ChangeKind? IgnoreOnceKind { get; init; }

        public int? RemoveSessionsOnCapture { get; init; }

        public int? ThrowOnCapture { get; init; }

        public (int Capture, ChangeKind Kind, string TargetId, double? Volume, bool? IsMuted)? MutateOnCapture { get; init; }

        public AdapterCapabilities Capabilities { get; } = new(true, true, true, null);

        public ValueTask<AudioSnapshot> CaptureAsync(
            AudioInventoryOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("capture");
            captureCount++;
            if (ThrowOnCapture == captureCount)
            {
                throw new InvalidOperationException("Injected capture exception.");
            }

            if (RemoveSessionsOnCapture == captureCount)
            {
                sessions.Clear();
            }

            if (MutateOnCapture is { } mutation && mutation.Capture == captureCount)
            {
                int index = endpoints.FindIndex(endpoint => endpoint.StableId == mutation.TargetId);
                if (index >= 0)
                {
                    endpoints[index] = mutation.Kind switch
                    {
                        ChangeKind.EndpointVolume => endpoints[index] with { Volume = new VolumeLevel(mutation.Volume!.Value) },
                        ChangeKind.EndpointMute => endpoints[index] with { IsMuted = mutation.IsMuted },
                        _ => endpoints[index],
                    };
                }
            }

            return ValueTask.FromResult(new AudioSnapshot(DateTimeOffset.UtcNow, [.. endpoints], [.. sessions]));
        }

        public async IAsyncEnumerable<AudioSnapshot> ObserveAsync(
            AudioInventoryOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await CaptureAsync(options, cancellationToken);
        }

        public ValueTask<ControlWriteResult> WriteAsync(
            AudioControlCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"write:{command.Kind}");
            attempts[command.Kind] = attempts.GetValueOrDefault(command.Kind) + 1;
            if (ThrowAttempt is { } throwing && throwing.Kind == command.Kind &&
                throwing.Attempt == attempts[command.Kind])
            {
                throw new InvalidOperationException("Injected adapter exception.");
            }

            if (FailOnceKind == command.Kind && attempts[command.Kind] == 1)
            {
                return ValueTask.FromResult(ControlWriteResult.Failure("Access denied.", 5));
            }

            if (FailAttempt is { } failure && failure.Kind == command.Kind && failure.Attempt == attempts[command.Kind])
            {
                return ValueTask.FromResult(ControlWriteResult.Failure("Injected rollback failure."));
            }

            if (IgnoreOnceKind == command.Kind && attempts[command.Kind] == 1)
            {
                return ValueTask.FromResult(ControlWriteResult.Success("Injected partial write."));
            }

            int endpointIndex = endpoints.FindIndex(endpoint => endpoint.StableId == command.TargetId);
            if (endpointIndex >= 0)
            {
                EndpointDescriptor endpoint = endpoints[endpointIndex];
                endpoints[endpointIndex] = command.Kind switch
                {
                    ChangeKind.DefaultRole => endpoint with
                    {
                        DefaultRoles = [.. endpoint.DefaultRoles.Append(command.Role!.Value)],
                    },
                    ChangeKind.EndpointVolume => endpoint with { Volume = command.Volume },
                    ChangeKind.EndpointMute => endpoint with { IsMuted = command.IsMuted },
                    _ => endpoint,
                };
                if (command.Kind == ChangeKind.DefaultRole)
                {
                    for (int index = 0; index < endpoints.Count; index++)
                    {
                        if (index != endpointIndex && endpoints[index].Direction == command.Direction)
                        {
                            endpoints[index] = endpoints[index] with
                            {
                                DefaultRoles = [.. endpoints[index].DefaultRoles.Where(role => role != command.Role)],
                            };
                        }
                    }
                }
            }

            int sessionIndex = sessions.FindIndex(session => session.SessionId == command.TargetId);
            if (sessionIndex >= 0)
            {
                SessionDescriptor session = sessions[sessionIndex];
                sessions[sessionIndex] = command.Kind switch
                {
                    ChangeKind.SessionVolume => session with { Volume = command.Volume },
                    ChangeKind.SessionMute => session with { IsMuted = command.IsMuted },
                    _ => session,
                };
            }

            if (attempts.Values.Sum() == 1)
            {
                CancelAfterFirstWrite?.Cancel();
            }

            return ValueTask.FromResult(ControlWriteResult.Success());
        }
    }
}
