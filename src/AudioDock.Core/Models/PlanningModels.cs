namespace AudioDock.Core.Models;

public sealed record PlannedChange(
    int Sequence,
    ChangeKind Kind,
    PlanDisposition Disposition,
    string Target,
    string? ResolvedTargetId,
    string? Before,
    string? After,
    string Explanation);

public sealed record ScenePlan(Guid SceneId, IReadOnlyList<PlannedChange> Changes)
{
    public bool HasActionableChanges => Changes.Any(change => change.Disposition == PlanDisposition.Apply);
}
