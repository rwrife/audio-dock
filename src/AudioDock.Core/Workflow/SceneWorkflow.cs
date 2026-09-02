using AudioDock.Core.Abstractions;
using AudioDock.Core.Execution;
using AudioDock.Core.Matching;
using AudioDock.Core.Models;
using AudioDock.Core.Persistence;

namespace AudioDock.Core.Workflow;

public interface ISceneWorkflow
{
    ValueTask<IReadOnlyList<AudioScene>> LoadScenesAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ActivityRecord>> LoadActivityAsync(CancellationToken cancellationToken = default);
    ValueTask<AudioScene> CaptureCurrentAsync(string name, CancellationToken cancellationToken = default);
    ValueTask SaveAsync(IReadOnlyCollection<AudioScene> values, CancellationToken cancellationToken = default);
    ValueTask<ScenePreview> PreviewAsync(AudioScene scene, CancellationToken cancellationToken = default);
    ValueTask<ApplyResult> ApplyAsync(ScenePreview preview, CancellationToken cancellationToken = default);
    ValueTask<RollbackFact> UndoAsync(Guid snapshotId, string sceneName);
    ValueTask ClearUndoAsync() => ValueTask.CompletedTask;
}

public sealed class SceneWorkflow(IAudioControlAdapter adapter, ISceneStore scenes, IActivityStore activities,
    Func<bool>? executablePathMatchingEnabled = null, IDiagnosticSink? diagnostics = null) : ISceneWorkflow
{
    private readonly SceneExecutor executor = new(adapter);

    public ValueTask<IReadOnlyList<AudioScene>> LoadScenesAsync(CancellationToken cancellationToken = default) => scenes.LoadAsync(cancellationToken);

    public ValueTask<IReadOnlyList<ActivityRecord>> LoadActivityAsync(CancellationToken cancellationToken = default) => activities.LoadAsync(cancellationToken);

    public async ValueTask<AudioScene> CaptureCurrentAsync(string name, CancellationToken cancellationToken = default)
    {
        AudioSnapshot snapshot = await adapter.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        EndpointDescriptor[] active = snapshot.Endpoints.Where(endpoint => endpoint.State == EndpointState.Active).ToArray();
        RoleTarget[] roles = active.SelectMany(endpoint => endpoint.DefaultRoles.Select(role =>
            new RoleTarget(role, EndpointRuleFor(endpoint)))).ToArray();
        EndpointRule[] endpointRules = active.Where(endpoint => endpoint.Volume is not null || endpoint.IsMuted is not null)
            .Select(endpoint => new EndpointRule(EndpointRuleFor(endpoint), endpoint.Volume, endpoint.IsMuted)).ToArray();
        ApplicationRule[] applicationRules = snapshot.Sessions.Where(session => session.State == SessionState.Active)
            .Select(session => new ApplicationRule(ApplicationRuleFor(session), session.Volume, session.IsMuted)).ToArray();
        return new(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), name, roles, endpointRules, applicationRules);
    }

    public async ValueTask SaveAsync(IReadOnlyCollection<AudioScene> values, CancellationToken cancellationToken = default) =>
        await scenes.SaveAsync(values, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ScenePreview> PreviewAsync(AudioScene scene, CancellationToken cancellationToken = default)
    {
        if (scene.ApplicationRules.Any(rule => rule.Match.ExecutablePath is not null) && executablePathMatchingEnabled?.Invoke() != true)
            throw new InvalidOperationException("This scene uses executable-path matching. Enable the explicit path-matching privacy setting before previewing it.");
        AudioScene reviewedScene = new(scene.SchemaVersion, scene.Id, scene.Name, scene.RoleTargets, scene.EndpointRules, scene.ApplicationRules);
        AudioSnapshot snapshot = await adapter.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        ScenePlan generatedPlan = Planning.ScenePlanner.Plan(reviewedScene, snapshot);
        ScenePlan plan = new(generatedPlan.SceneId, generatedPlan.Changes.ToArray());
        var reviews = new List<MatchReview>();
        foreach (EndpointMatchRule rule in reviewedScene.RoleTargets.Select(item => item.Endpoint).Concat(reviewedScene.EndpointRules.Select(item => item.Match)).Distinct())
        {
            MatchResult<EndpointDescriptor> match = EndpointMatcher.Match(rule, snapshot.Endpoints);
            reviews.Add(ToReview(Describe(rule), "Endpoint", match.Status, match.Selected?.Score, match.Selected?.Reasons ?? [],
                match.Candidates.Select(item => item.Value.FriendlyName).ToArray(), match.Selected?.Value.Capabilities.ToString() ?? "Capability unavailable"));
        }

        foreach (ApplicationMatchRule rule in reviewedScene.ApplicationRules.Select(item => item.Match).Distinct())
        {
            MatchResult<SessionDescriptor> match = ApplicationMatcher.Match(rule, snapshot.Sessions);
            reviews.Add(ToReview(Describe(rule), "Application", match.Status, match.Selected?.Score, match.Selected?.Reasons ?? [],
                match.Candidates.Select(item => item.Value.ProcessName).ToArray(), match.Selected?.Value.Capabilities.ToString() ?? "Capability unavailable"));
        }

        return new(reviewedScene, plan, reviews, adapter.Capabilities);
    }

    public async ValueTask<ApplyResult> ApplyAsync(ScenePreview preview, CancellationToken cancellationToken = default)
    {
        ApplyResult result = await executor.ApplyAsync(preview.ReviewedScene, preview.Plan, cancellationToken).ConfigureAwait(false);
        try
        {
            await activities.AppendAsync(new(Guid.NewGuid(), result.SceneId, preview.ReviewedScene.Name, result.CompletedAt,
                $"Apply {result.State}: {result.Operations.Count} operation details retained.", result), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = result with { ActivityPersistenceFailure = $"Activity history could not be saved: {exception.Message}" };
        }
        await diagnostics.TryAppendAsync("scene_apply", $"Apply completed with state {result.State} and {result.Operations.Count} operations.");
        return result;
    }

    public async ValueTask<RollbackFact> UndoAsync(Guid snapshotId, string sceneName)
    {
        RollbackFact result = await executor.UndoAsync(snapshotId).ConfigureAwait(false);
        try
        {
            await activities.AppendAsync(new(Guid.NewGuid(), null, sceneName, DateTimeOffset.UtcNow, $"Undo {result.State}: {result.Detail}", UndoResult: result)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = result with { ActivityPersistenceFailure = $"Activity history could not be saved: {exception.Message}" };
        }
        await diagnostics.TryAppendAsync("scene_undo", $"Undo completed with state {result.State}.");
        return result;
    }

    public ValueTask ClearUndoAsync() { executor.ClearUndo(); return ValueTask.CompletedTask; }

    private static EndpointMatchRule EndpointRuleFor(EndpointDescriptor endpoint) =>
        new(endpoint.Direction, endpoint.StableId, InterfaceId: endpoint.InterfaceId, ContainerId: endpoint.ContainerId,
            FriendlyName: endpoint.FriendlyName, Manufacturer: endpoint.Manufacturer, Product: endpoint.Product);

    private static ApplicationMatchRule ApplicationRuleFor(SessionDescriptor session) =>
        new(PackageFamilyName: session.PackageFamilyName, Publisher: session.Publisher,
            ProductName: session.ProductName, ProcessName: session.ProcessName);

    private static MatchReview ToReview(string target, string kind, MatchStatus status, int? score,
        IReadOnlyList<string> reasons, IReadOnlyList<string> candidates, string capability) =>
        new(target, kind, status, score, status switch
        {
            MatchStatus.Matched => reasons.Count == 0 ? "Matched" : $"Matched by {string.Join(", ", reasons)}",
            MatchStatus.Ambiguous => "Ambiguous — review required",
            _ => "Missing — target is unavailable",
        }, reasons, candidates, capability);

    private static string Describe(EndpointMatchRule rule) => rule.UserAlias ?? rule.FriendlyName ?? rule.ExactId ?? $"{rule.Direction} endpoint";
    private static string Describe(ApplicationMatchRule rule) => rule.UserAlias ?? rule.ProductName ?? rule.ProcessName ?? rule.PackageFamilyName ?? "application";
}
