namespace AudioDock.Core.Models;

public sealed record OperationResult(
    int Sequence,
    ChangeKind Kind,
    string Target,
    OperationResultState State,
    string Detail,
    string? ObservedValue = null);

public sealed record RollbackFact(
    RollbackState State,
    Guid? SnapshotId,
    string Detail);

public sealed record ApplyResult(
    Guid SceneId,
    ApplyOverallState State,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<OperationResult> Operations,
    RollbackFact Rollback);
