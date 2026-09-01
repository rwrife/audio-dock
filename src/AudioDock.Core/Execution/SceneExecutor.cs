using AudioDock.Core.Abstractions;
using AudioDock.Core.Matching;
using AudioDock.Core.Models;
using AudioDock.Core.Planning;

namespace AudioDock.Core.Execution;

public sealed class SceneExecutor
{
    private const double VolumeTolerance = 0.005;
    private readonly IAudioControlAdapter adapter;
    private UndoSnapshot? undo;

    public SceneExecutor(IAudioControlAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public async ValueTask<ScenePlan> PreviewAsync(
        AudioScene scene,
        CancellationToken cancellationToken = default) =>
        ScenePlanner.Plan(scene, await adapter.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false));

    public async ValueTask<ApplyResult> ApplyAsync(
        AudioScene scene,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        AudioSnapshot preState = await adapter.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        ScenePlan plan = ScenePlanner.Plan(scene, preState);
        return await ExecutePlanAsync(plan, startedAt, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplyResult> ApplyAsync(
        AudioScene reviewedScene,
        ScenePlan reviewedPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewedScene);
        ArgumentNullException.ThrowIfNull(reviewedPlan);
        if (reviewedScene.Id != reviewedPlan.SceneId)
        {
            throw new StaleScenePreviewException("The reviewed scene and plan do not match. Preview the scene again.");
        }
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        AudioSnapshot preState = await adapter.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        ScenePlan currentPlan = ScenePlanner.Plan(reviewedScene, preState);
        if (!PlansMatch(reviewedPlan, currentPlan))
        {
            throw new StaleScenePreviewException("Audio state changed after preview. Review a new preview before applying; no writes were made.");
        }

        return await ExecutePlanAsync(reviewedPlan, startedAt, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ApplyResult> ExecutePlanAsync(ScenePlan plan, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var results = new List<OperationResult>(plan.Changes.Count);
        var rollbackCommands = new List<AudioControlCommand>();

        foreach (PlannedChange change in plan.Changes)
        {
            if (change.Disposition != PlanDisposition.Apply)
            {
                results.Add(new(change.Sequence, change.Kind, change.Target,
                    change.Disposition == PlanDisposition.NoOp ? OperationResultState.NoOp : DeferredOrSkipped(change),
                    change.Explanation, change.Before));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Skipped,
                    "Canceled at a safe operation boundary."));
                AppendNotAttempted(plan, results, change.Sequence);
                return await FinishInterruptedAsync(plan.SceneId, startedAt, results, rollbackCommands, true).ConfigureAwait(false);
            }

            (AudioSnapshot? current, string? captureFailure) = await CaptureSafelyAsync().ConfigureAwait(false);
            if (current is null)
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Failed,
                    captureFailure!));
                AppendNotAttempted(plan, results, change.Sequence);
                return await FinishInterruptedAsync(plan.SceneId, startedAt, results, rollbackCommands, false).ConfigureAwait(false);
            }

            AudioControlCommand? command = Resolve(change, current, out string? resolutionFailure);
            if (command is null)
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Skipped,
                    resolutionFailure ?? "The target could not be resolved immediately before use."));
                continue;
            }

            if (!StillMatchesReviewedBefore(change, command, current))
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Failed,
                    "Audio state changed after preview at the final write boundary. Re-preview before applying; this write was not made."));
                AppendNotAttempted(plan, results, change.Sequence);
                return await FinishInterruptedAsync(plan.SceneId, startedAt, results, rollbackCommands, false).ConfigureAwait(false);
            }

            AudioControlCommand? rollback = CreateRollbackCommand(command, current);
            if (rollback is null)
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Skipped,
                    "The pre-apply state is unavailable, so a safe rollback cannot be prepared."));
                continue;
            }

            rollbackCommands.Add(rollback);

            ControlWriteResult write = await WriteSafelyAsync(command).ConfigureAwait(false);
            if (!write.Succeeded)
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Failed, write.Detail));
                AppendNotAttempted(plan, results, change.Sequence);
                return await FinishInterruptedAsync(plan.SceneId, startedAt, results, rollbackCommands, false).ConfigureAwait(false);
            }

            (AudioSnapshot? observed, captureFailure) = await CaptureSafelyAsync().ConfigureAwait(false);
            if (observed is null)
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Failed,
                    captureFailure!));
                AppendNotAttempted(plan, results, change.Sequence);
                return await FinishInterruptedAsync(plan.SceneId, startedAt, results, rollbackCommands, false).ConfigureAwait(false);
            }

            if (!Verify(command, observed, out string observedValue))
            {
                results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.VerificationMismatch,
                    "The observable state did not match the requested value.", observedValue));
                AppendNotAttempted(plan, results, change.Sequence);
                return await FinishInterruptedAsync(plan.SceneId, startedAt, results, rollbackCommands, false).ConfigureAwait(false);
            }

            results.Add(new(change.Sequence, change.Kind, change.Target, OperationResultState.Applied,
                write.Detail, observedValue));
        }

        if (rollbackCommands.Count > 0)
        {
            undo = new(Guid.NewGuid(), [.. rollbackCommands]);
        }
        else
        {
            undo = null;
        }

        ApplyOverallState state = results.Any(result => result.State is OperationResultState.Skipped or OperationResultState.Deferred)
            ? ApplyOverallState.Partial
            : ApplyOverallState.Applied;
        RollbackFact rollbackFact = undo is null
            ? new(RollbackState.NotRequired, null, "No audio state changed.")
            : new(RollbackState.Available, undo.Id, "One bounded pre-apply snapshot is available for undo.");
        return new(plan.SceneId, state, startedAt, DateTimeOffset.UtcNow, results, rollbackFact);
    }

    private static bool PlansMatch(ScenePlan reviewed, ScenePlan current) =>
        reviewed.SceneId == current.SceneId && reviewed.Changes.SequenceEqual(current.Changes);

    public async ValueTask<RollbackFact> UndoAsync(Guid snapshotId)
    {
        if (undo is null || undo.Id != snapshotId)
        {
            return new(RollbackState.Cleared, null, "That undo snapshot is unavailable or has already been consumed.");
        }

        UndoSnapshot snapshot = undo;
        undo = null;
        RollbackFact result = await RollBackAsync(snapshot.Commands).ConfigureAwait(false);
        return result with { SnapshotId = snapshot.Id };
    }

    public void ClearUndo() => undo = null;

    private static void AppendNotAttempted(ScenePlan plan, List<OperationResult> results, int afterSequence)
    {
        foreach (PlannedChange remaining in plan.Changes.Where(change => change.Sequence > afterSequence))
        {
            results.Add(new(remaining.Sequence, remaining.Kind, remaining.Target, OperationResultState.Skipped,
                "Not attempted because the transaction stopped at an earlier operation."));
        }
    }

    private async ValueTask<ApplyResult> FinishInterruptedAsync(
        Guid sceneId,
        DateTimeOffset startedAt,
        List<OperationResult> results,
        List<AudioControlCommand> rollbackCommands,
        bool canceled)
    {
        undo = null;
        RollbackFact rollback = await RollBackAsync(rollbackCommands).ConfigureAwait(false);
        if (rollback.State is RollbackState.Succeeded or RollbackState.Failed)
        {
            OperationResultState rollbackResult = rollback.State == RollbackState.Succeeded
                ? OperationResultState.RolledBack
                : OperationResultState.RollbackFailed;
            for (int index = 0; index < results.Count; index++)
            {
                if (results[index].State == OperationResultState.Applied)
                {
                    results[index] = results[index] with { State = rollbackResult };
                }
            }
        }

        ApplyOverallState state = rollback.State switch
        {
            RollbackState.Failed => ApplyOverallState.Partial,
            _ when canceled => ApplyOverallState.Canceled,
            RollbackState.Succeeded => ApplyOverallState.RolledBack,
            _ => ApplyOverallState.Failed,
        };
        return new(sceneId, state, startedAt, DateTimeOffset.UtcNow, results, rollback);
    }

    private async ValueTask<ControlWriteResult> WriteSafelyAsync(AudioControlCommand command)
    {
        try
        {
            return await adapter.WriteAsync(command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return ControlWriteResult.Failure($"The audio control adapter threw: {exception.Message}", exception.HResult);
        }
    }

    private async ValueTask<(AudioSnapshot? Snapshot, string? Failure)> CaptureSafelyAsync()
    {
        try
        {
            return (await adapter.CaptureAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false), null);
        }
        catch (Exception exception)
        {
            return (null, $"The audio control adapter threw during read-back: {exception.Message}");
        }
    }

    private async ValueTask<RollbackFact> RollBackAsync(IReadOnlyList<AudioControlCommand> commands)
    {
        if (commands.Count == 0)
        {
            return new(RollbackState.NotRequired, null, "No reversible write completed.");
        }

        bool failed = false;
        foreach (AudioControlCommand command in commands.Reverse())
        {
            ControlWriteResult result = await WriteSafelyAsync(command).ConfigureAwait(false);
            (AudioSnapshot? observed, _) = await CaptureSafelyAsync().ConfigureAwait(false);
            failed |= !result.Succeeded || observed is null || !Verify(command, observed, out _);
        }

        return failed
            ? new(RollbackState.Failed, null, "Best-effort rollback did not restore every observable value.")
            : new(RollbackState.Succeeded, null, "Best-effort rollback restored every observable value.");
    }

    private static OperationResultState DeferredOrSkipped(PlannedChange change) =>
        change.ApplicationMatch is not null && change.ResolvedTargetId is null &&
        change.Explanation.Contains("deferred", StringComparison.OrdinalIgnoreCase)
            ? OperationResultState.Deferred
            : OperationResultState.Skipped;

    private static AudioControlCommand? Resolve(
        PlannedChange change,
        AudioSnapshot snapshot,
        out string? failure)
    {
        failure = null;
        if (change.EndpointMatch is not null)
        {
            MatchResult<EndpointDescriptor> match = EndpointMatcher.Match(change.EndpointMatch, snapshot.Endpoints);
            if (match.Status != MatchStatus.Matched)
            {
                failure = match.Status == MatchStatus.Ambiguous
                    ? "The endpoint became ambiguous immediately before use."
                    : "The endpoint was stale or unavailable immediately before use.";
                return null;
            }

            EndpointDescriptor endpoint = match.Selected!.Value;
            if (!Supports(endpoint, change.Kind))
            {
                failure = "The endpoint no longer reports the required capability.";
                return null;
            }

            return new(change.Kind, endpoint.StableId, change.Direction, change.Role, change.Volume, change.IsMuted);
        }

        if (change.ApplicationMatch is not null)
        {
            MatchResult<SessionDescriptor> match = ApplicationMatcher.Match(change.ApplicationMatch, snapshot.Sessions);
            if (match.Status != MatchStatus.Matched)
            {
                failure = match.Status == MatchStatus.Ambiguous
                    ? "The application session became ambiguous immediately before use."
                    : "The application session disappeared immediately before use.";
                return null;
            }

            SessionDescriptor session = match.Selected!.Value;
            if (!Supports(session, change.Kind))
            {
                failure = "The application session no longer reports the required capability.";
                return null;
            }

            return new(change.Kind, session.SessionId, Volume: change.Volume, IsMuted: change.IsMuted);
        }

        failure = "The plan did not retain a match rule for re-resolution.";
        return null;
    }

    private static bool Supports(EndpointDescriptor endpoint, ChangeKind kind) => kind switch
    {
        ChangeKind.DefaultRole => endpoint.Capabilities.CanSetDefaultRole,
        ChangeKind.EndpointVolume => endpoint.Capabilities.CanSetVolume,
        ChangeKind.EndpointMute => endpoint.Capabilities.CanSetMute,
        _ => false,
    };

    private static bool Supports(SessionDescriptor session, ChangeKind kind) => kind switch
    {
        ChangeKind.SessionVolume => session.Capabilities.CanSetVolume,
        ChangeKind.SessionMute => session.Capabilities.CanSetMute,
        _ => false,
    };

    private static bool StillMatchesReviewedBefore(
        PlannedChange change,
        AudioControlCommand command,
        AudioSnapshot snapshot)
    {
        if (change.ResolvedTargetId != command.TargetId) return false;

        if (command.Kind == ChangeKind.DefaultRole)
        {
            string owner = snapshot.Endpoints.FirstOrDefault(endpoint =>
                endpoint.Direction == command.Direction && command.Role is not null &&
                endpoint.DefaultRoles.Contains(command.Role.Value))?.StableId ?? "none";
            return owner == change.Before;
        }

        EndpointDescriptor? endpoint = snapshot.Endpoints.FirstOrDefault(candidate => candidate.StableId == command.TargetId);
        SessionDescriptor? session = snapshot.Sessions.FirstOrDefault(candidate => candidate.SessionId == command.TargetId);
        string current = command.Kind switch
        {
            ChangeKind.EndpointVolume => endpoint?.Volume?.ToString() ?? "unknown",
            ChangeKind.EndpointMute => Format(endpoint?.IsMuted),
            ChangeKind.SessionVolume => session?.Volume?.ToString() ?? "unknown",
            ChangeKind.SessionMute => Format(session?.IsMuted),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null),
        };
        return current == change.Before;
    }

    private static AudioControlCommand? CreateRollbackCommand(AudioControlCommand command, AudioSnapshot snapshot)
    {
        if (command.Kind == ChangeKind.DefaultRole)
        {
            EndpointDescriptor? previous = snapshot.Endpoints.FirstOrDefault(endpoint =>
                endpoint.Direction == command.Direction && command.Role is not null && endpoint.DefaultRoles.Contains(command.Role.Value));
            return previous is null ? null : command with { TargetId = previous.StableId };
        }

        EndpointDescriptor? endpoint = snapshot.Endpoints.FirstOrDefault(candidate => candidate.StableId == command.TargetId);
        if (endpoint is not null)
        {
            return command.Kind switch
            {
                ChangeKind.EndpointVolume when endpoint.Volume is not null => command with { Volume = endpoint.Volume },
                ChangeKind.EndpointMute when endpoint.IsMuted is not null => command with { IsMuted = endpoint.IsMuted },
                _ => null,
            };
        }

        SessionDescriptor? session = snapshot.Sessions.FirstOrDefault(candidate => candidate.SessionId == command.TargetId);
        return command.Kind switch
        {
            ChangeKind.SessionVolume when session?.Volume is not null => command with { Volume = session.Volume },
            ChangeKind.SessionMute when session?.IsMuted is not null => command with { IsMuted = session.IsMuted },
            _ => null,
        };
    }

    private static bool Verify(AudioControlCommand command, AudioSnapshot snapshot, out string observedValue)
    {
        EndpointDescriptor? endpoint = snapshot.Endpoints.FirstOrDefault(candidate => candidate.StableId == command.TargetId);
        SessionDescriptor? session = snapshot.Sessions.FirstOrDefault(candidate => candidate.SessionId == command.TargetId);
        switch (command.Kind)
        {
            case ChangeKind.DefaultRole:
                bool ownsRole = endpoint is not null && command.Role is not null && endpoint.DefaultRoles.Contains(command.Role.Value);
                observedValue = ownsRole ? command.TargetId : "role target differs or is unavailable";
                return ownsRole;
            case ChangeKind.EndpointVolume:
                observedValue = endpoint?.Volume?.ToString() ?? "unknown";
                return VolumesEqual(endpoint?.Volume, command.Volume);
            case ChangeKind.EndpointMute:
                observedValue = Format(endpoint?.IsMuted);
                return endpoint?.IsMuted == command.IsMuted;
            case ChangeKind.SessionVolume:
                observedValue = session?.Volume?.ToString() ?? "unknown";
                return VolumesEqual(session?.Volume, command.Volume);
            case ChangeKind.SessionMute:
                observedValue = Format(session?.IsMuted);
                return session?.IsMuted == command.IsMuted;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null);
        }
    }

    private static bool VolumesEqual(VolumeLevel? observed, VolumeLevel? desired) =>
        observed is not null && desired is not null &&
        Math.Abs(observed.Value.Value - desired.Value.Value) <= VolumeTolerance;

    private static string Format(bool? value) => value switch
    {
        true => "muted",
        false => "unmuted",
        null => "unknown",
    };

    private sealed record UndoSnapshot(Guid Id, IReadOnlyList<AudioControlCommand> Commands);
}
