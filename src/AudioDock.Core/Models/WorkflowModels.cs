namespace AudioDock.Core.Models;

public sealed record ActivityRecord(
    Guid Id,
    Guid? SceneId,
    string SceneName,
    DateTimeOffset OccurredAt,
    string Summary,
    ApplyResult? ApplyResult = null,
    RollbackFact? UndoResult = null);

public sealed record MatchReview(
    string Target,
    string Kind,
    MatchStatus Status,
    int? Score,
    string Quality,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Candidates,
    string Capability);

public sealed record ScenePreview(AudioScene ReviewedScene, ScenePlan Plan, IReadOnlyList<MatchReview> Matches, AdapterCapabilities Capabilities)
{
    public bool RequiresResolution => Matches.Any(match => match.Status is MatchStatus.Unmatched or MatchStatus.Ambiguous);
}
