using AudioDock.Core.Models;
using AudioDock.Core.Planning;

namespace AudioDock.Core.Tests.Planning;

public sealed class ScenePlannerTests
{
    [Fact]
    public void OrdersRolesBeforeEndpointChangesBeforeSessionChanges()
    {
        EndpointDescriptor oldDefault = TestData.Endpoint(
            "old",
            roles: [AudioRole.Multimedia],
            volume: new VolumeLevel(0.4));
        EndpointDescriptor target = TestData.Endpoint(
            "new",
            volume: new VolumeLevel(0.2),
            muted: false);
        SessionDescriptor session = TestData.Session(
            "session",
            packageFamily: "Contoso.Meet_abc",
            volume: new VolumeLevel(0.3),
            muted: false);
        AudioScene scene = Scene(
            roles: [new(AudioRole.Multimedia, new(AudioDirection.Render, ExactId: "new"))],
            endpoints: [new(new(AudioDirection.Render, ExactId: "new"), new VolumeLevel(0.7), true)],
            applications: [new(new(PackageFamilyName: "Contoso.Meet_abc"), new VolumeLevel(0.8), true)]);

        ScenePlan plan = ScenePlanner.Plan(scene, Snapshot([target, oldDefault], [session]));

        Assert.Equal(
            [ChangeKind.DefaultRole, ChangeKind.EndpointVolume, ChangeKind.EndpointMute, ChangeKind.SessionVolume, ChangeKind.SessionMute],
            plan.Changes.Select(change => change.Kind));
        Assert.All(plan.Changes, change => Assert.Equal(PlanDisposition.Apply, change.Disposition));
        Assert.Equal([1, 2, 3, 4, 5], plan.Changes.Select(change => change.Sequence));
        Assert.True(plan.HasActionableChanges);
    }

    [Fact]
    public void PlanningAlreadySatisfiedSceneIsIdempotent()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "speakers",
            roles: [AudioRole.Multimedia],
            volume: new VolumeLevel(0.5),
            muted: false);
        SessionDescriptor session = TestData.Session(
            "music",
            processName: "music",
            volume: new VolumeLevel(0.25),
            muted: true);
        AudioScene scene = Scene(
            roles: [new(AudioRole.Multimedia, new(AudioDirection.Render, ExactId: "speakers"))],
            endpoints: [new(new(AudioDirection.Render, ExactId: "speakers"), new VolumeLevel(0.5), false)],
            applications: [new(new(ProcessName: "music"), new VolumeLevel(0.25), true)]);

        ScenePlan first = ScenePlanner.Plan(scene, Snapshot([endpoint], [session]));
        ScenePlan second = ScenePlanner.Plan(scene, Snapshot([endpoint], [session]));

        Assert.All(first.Changes, change => Assert.Equal(PlanDisposition.NoOp, change.Disposition));
        Assert.False(first.HasActionableChanges);
        Assert.Equal(first.SceneId, second.SceneId);
        Assert.True(first.Changes.SequenceEqual(second.Changes));
    }

    [Fact]
    public void StaleEndpointIsSkippedWithExplicitReason()
    {
        EndpointDescriptor unplugged = TestData.Endpoint("unplugged", state: EndpointState.Unplugged);
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "unplugged"), new VolumeLevel(0.5))]);

        PlannedChange change = Assert.Single(ScenePlanner.Plan(scene, Snapshot([unplugged], [])).Changes);

        Assert.Equal(PlanDisposition.Skipped, change.Disposition);
        Assert.Contains("No endpoint matched", change.Explanation, StringComparison.Ordinal);
        Assert.Null(change.ResolvedTargetId);
    }

    [Fact]
    public void AmbiguousFriendlyNamesAreSkippedInsteadOfSelected()
    {
        EndpointDescriptor first = TestData.Endpoint("one", name: "USB Audio");
        EndpointDescriptor second = TestData.Endpoint("two", name: "USB Audio");
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, FriendlyName: "USB Audio"), IsMuted: true)]);

        PlannedChange change = Assert.Single(ScenePlanner.Plan(scene, Snapshot([first, second], [])).Changes);

        Assert.Equal(PlanDisposition.Skipped, change.Disposition);
        Assert.Contains("Multiple endpoints", change.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedControlIsReportedAsSkipped()
    {
        EndpointDescriptor endpoint = TestData.Endpoint(
            "read-only",
            capabilities: EndpointCapabilities.ReadOnly,
            volume: new VolumeLevel(0.2));
        AudioScene scene = Scene(
            endpoints: [new(new(AudioDirection.Render, ExactId: "read-only"), new VolumeLevel(0.8))]);

        PlannedChange change = Assert.Single(ScenePlanner.Plan(scene, Snapshot([endpoint], [])).Changes);

        Assert.Equal(PlanDisposition.Skipped, change.Disposition);
        Assert.Contains("unavailable", change.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void NonRunningApplicationRuleIsExplicitlyDeferredButNotApplied()
    {
        AudioScene scene = Scene(
            applications: [new(new(ProcessName: "meeting"), new VolumeLevel(0.8))]);

        PlannedChange change = Assert.Single(ScenePlanner.Plan(scene, AudioSnapshot.Empty).Changes);

        Assert.Equal(PlanDisposition.Skipped, change.Disposition);
        Assert.Contains("deferred rule is not reported as applied", change.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSceneVersionIsRejectedBeforePlanning()
    {
        AudioScene scene = new(99, Guid.NewGuid(), "Future");

        Assert.Throws<NotSupportedException>(() => ScenePlanner.Plan(scene, AudioSnapshot.Empty));
    }

    private static AudioScene Scene(
        IEnumerable<RoleTarget>? roles = null,
        IEnumerable<EndpointRule>? endpoints = null,
        IEnumerable<ApplicationRule>? applications = null) =>
        new(AudioScene.CurrentSchemaVersion, Guid.Parse("11111111-1111-1111-1111-111111111111"), "Test", roles, endpoints, applications);

    private static AudioSnapshot Snapshot(
        IReadOnlyList<EndpointDescriptor> endpoints,
        IReadOnlyList<SessionDescriptor> sessions) =>
        new(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero), endpoints, sessions);
}
