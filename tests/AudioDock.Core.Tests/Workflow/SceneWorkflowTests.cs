using System.Runtime.CompilerServices;
using AudioDock.Core.Abstractions;
using AudioDock.Core.Models;
using AudioDock.Core.Workflow;

namespace AudioDock.Core.Tests.Workflow;

public sealed class SceneWorkflowTests
{
    [Fact]
    public async Task CaptureCurrentCreatesPortableRulesWithoutExecutablePathsOrAudioContent()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speaker", name: "Desk", volume: new(0.4), roles: [AudioRole.Multimedia]);
        SessionDescriptor session = TestData.Session("music", processName: "player", volume: new(0.3), path: "C:\\Private\\player.exe");
        var workflow = Create(new FakeAdapter([endpoint], [session]), out _, out _);

        AudioScene scene = await workflow.CaptureCurrentAsync("Desk");

        Assert.Single(scene.RoleTargets);
        Assert.Single(scene.EndpointRules);
        ApplicationRule application = Assert.Single(scene.ApplicationRules);
        Assert.Equal("player", application.Match.ProcessName);
        Assert.Null(application.Match.ExecutablePath);
    }

    [Fact]
    public async Task PreviewReportsAmbiguousMatchQualityCandidatesAndBeforeAfterPlan()
    {
        EndpointDescriptor one = TestData.Endpoint("one", name: "USB");
        EndpointDescriptor two = TestData.Endpoint("two", name: "USB");
        var workflow = Create(new FakeAdapter([one, two], []), out _, out _);
        AudioScene scene = new(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Ambiguous",
            endpointRules: [new(new(AudioDirection.Render, FriendlyName: "USB"), new(0.6))]);

        ScenePreview preview = await workflow.PreviewAsync(scene);

        MatchReview review = Assert.Single(preview.Matches);
        Assert.Equal(MatchStatus.Ambiguous, review.Status);
        Assert.Equal(2, review.Candidates.Count);
        Assert.True(preview.RequiresResolution);
        PlannedChange change = Assert.Single(preview.Plan.Changes);
        Assert.Equal("0.6", change.After);
        Assert.Equal(PlanDisposition.Skipped, change.Disposition);
    }

    [Fact]
    public async Task ApplyPersistsCompletePartialResultDetails()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("read-only", capabilities: EndpointCapabilities.ReadOnly, volume: new(0.2));
        var workflow = Create(new FakeAdapter([endpoint], []), out _, out MemoryActivityStore activity);
        AudioScene scene = new(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Limited",
            endpointRules: [new(new(AudioDirection.Render, ExactId: "read-only"), new(0.8))]);

        ScenePreview preview = await workflow.PreviewAsync(scene);
        ApplyResult result = await workflow.ApplyAsync(preview);

        Assert.Equal(ApplyOverallState.Partial, result.State);
        ActivityRecord saved = Assert.Single(activity.Values);
        Assert.Equal(OperationResultState.Skipped, Assert.Single(saved.ApplyResult!.Operations).State);
        Assert.Contains("unavailable", saved.ApplyResult.Operations[0].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyReturnsResultAndUndoWhenActivityPersistenceFails()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speaker", volume: new(0.2));
        var adapter = new MutableAdapter(endpoint);
        var workflow = new SceneWorkflow(adapter, new MemorySceneStore(), new ThrowingActivityStore());
        AudioScene scene = new(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Desk",
            endpointRules: [new(new(AudioDirection.Render, ExactId: "speaker"), new(0.8))]);
        ScenePreview preview = await workflow.PreviewAsync(scene);

        ApplyResult result = await workflow.ApplyAsync(preview);

        Assert.Equal(ApplyOverallState.Applied, result.State);
        Assert.NotNull(result.Rollback.SnapshotId);
        Assert.Contains("could not be saved", result.ActivityPersistenceFailure);
        RollbackFact undo = await workflow.UndoAsync(result.Rollback.SnapshotId!.Value, scene.Name);
        Assert.Equal(RollbackState.Succeeded, undo.State);
    }

    [Fact]
    public async Task UndoReturnsCompletedRestorationWithWarningWhenActivityPersistenceFails()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speaker", volume: new(0.2));
        var adapter = new MutableAdapter(endpoint);
        var workflow = new SceneWorkflow(adapter, new MemorySceneStore(), new FailSecondAppendActivityStore());
        AudioScene scene = new(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Desk",
            endpointRules: [new(new(AudioDirection.Render, ExactId: "speaker"), new(0.8))]);
        ApplyResult applied = await workflow.ApplyAsync(await workflow.PreviewAsync(scene));

        RollbackFact undo = await workflow.UndoAsync(applied.Rollback.SnapshotId!.Value, scene.Name);

        Assert.Equal(RollbackState.Succeeded, undo.State);
        Assert.Contains("could not be saved", undo.ActivityPersistenceFailure);
        Assert.Equal(new VolumeLevel(0.2), adapter.Endpoint.Volume);
    }

    [Fact]
    public async Task ApplyRejectsChangedPlanBeforeAnyWrite()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("speaker", volume: new(0.2));
        var adapter = new MutableAdapter(endpoint);
        var workflow = Create(adapter, out _, out _);
        AudioScene scene = new(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Desk",
            endpointRules: [new(new(AudioDirection.Render, ExactId: "speaker"), new(0.8))]);
        ScenePreview preview = await workflow.PreviewAsync(scene);
        adapter.Endpoint = endpoint with { Volume = new(0.4) };

        await Assert.ThrowsAsync<AudioDock.Core.Execution.StaleScenePreviewException>(async () => await workflow.ApplyAsync(preview));
        Assert.Equal(0, adapter.WriteCount);
    }

    private static SceneWorkflow Create(IAudioControlAdapter adapter, out MemorySceneStore scenes, out MemoryActivityStore activity)
    {
        scenes = new(); activity = new(); return new(adapter, scenes, activity);
    }

    private sealed class ThrowingActivityStore : IActivityStore
    {
        private int appends;
        public ValueTask<IReadOnlyList<ActivityRecord>> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ActivityRecord>>([]);
        public ValueTask AppendAsync(ActivityRecord activity, CancellationToken cancellationToken = default) =>
            appends++ == 0 ? ValueTask.FromException(new IOException("disk full")) : ValueTask.CompletedTask;
    }

    private sealed class FailSecondAppendActivityStore : IActivityStore
    {
        private int appends;
        public ValueTask<IReadOnlyList<ActivityRecord>> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ActivityRecord>>([]);
        public ValueTask AppendAsync(ActivityRecord activity, CancellationToken cancellationToken = default) =>
            appends++ == 0 ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("disk full"));
    }

    private sealed class MutableAdapter(EndpointDescriptor endpoint) : IAudioControlAdapter
    {
        public EndpointDescriptor Endpoint { get; set; } = endpoint;
        public int WriteCount { get; private set; }
        public AdapterCapabilities Capabilities { get; } = new(true, true, true, null);
        public ValueTask<AudioSnapshot> CaptureAsync(AudioInventoryOptions? options = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(new AudioSnapshot(DateTimeOffset.UtcNow, [Endpoint], []));
        public async IAsyncEnumerable<AudioSnapshot> ObserveAsync(AudioInventoryOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) { yield return await CaptureAsync(options, cancellationToken); }
        public ValueTask<ControlWriteResult> WriteAsync(AudioControlCommand command, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            Endpoint = command.Kind switch { ChangeKind.EndpointVolume => Endpoint with { Volume = command.Volume }, ChangeKind.EndpointMute => Endpoint with { IsMuted = command.IsMuted }, _ => Endpoint };
            return ValueTask.FromResult(ControlWriteResult.Success("written"));
        }
    }

    private sealed class MemorySceneStore : ISceneStore
    {
        public IReadOnlyList<AudioScene> Values { get; private set; } = [];
        public ValueTask<IReadOnlyList<AudioScene>> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Values);
        public ValueTask SaveAsync(IReadOnlyCollection<AudioScene> scenes, CancellationToken cancellationToken = default) { Values = [.. scenes]; return ValueTask.CompletedTask; }
    }

    private sealed class MemoryActivityStore : IActivityStore
    {
        public List<ActivityRecord> Values { get; } = [];
        public ValueTask<IReadOnlyList<ActivityRecord>> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ActivityRecord>>(Values);
        public ValueTask AppendAsync(ActivityRecord activity, CancellationToken cancellationToken = default) { Values.Add(activity); return ValueTask.CompletedTask; }
    }

    private sealed class FakeAdapter(IReadOnlyList<EndpointDescriptor> endpoints, IReadOnlyList<SessionDescriptor> sessions) : IAudioControlAdapter
    {
        public AdapterCapabilities Capabilities { get; } = new(true, true, true, null);
        public ValueTask<AudioSnapshot> CaptureAsync(AudioInventoryOptions? options = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(new AudioSnapshot(DateTimeOffset.UtcNow, endpoints, sessions));
        public async IAsyncEnumerable<AudioSnapshot> ObserveAsync(AudioInventoryOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) { yield return await CaptureAsync(options, cancellationToken); }
        public ValueTask<ControlWriteResult> WriteAsync(AudioControlCommand command, CancellationToken cancellationToken = default) => ValueTask.FromResult(ControlWriteResult.Failure("not expected"));
    }
}
