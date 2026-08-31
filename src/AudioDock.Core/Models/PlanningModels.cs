namespace AudioDock.Core.Models;

public sealed record PlannedChange(
    int Sequence,
    ChangeKind Kind,
    PlanDisposition Disposition,
    string Target,
    string? ResolvedTargetId,
    string? Before,
    string? After,
    string Explanation,
    EndpointMatchRule? EndpointMatch = null,
    ApplicationMatchRule? ApplicationMatch = null,
    AudioDirection? Direction = null,
    AudioRole? Role = null,
    VolumeLevel? Volume = null,
    bool? IsMuted = null);

public sealed record ScenePlan(Guid SceneId, IReadOnlyList<PlannedChange> Changes)
{
    public bool HasActionableChanges => Changes.Any(change => change.Disposition == PlanDisposition.Apply);
}
