using AudioDock.Core.Models;

namespace AudioDock.Core.Matching;

public sealed record MatchCandidate<T>(
    T Value,
    string StableKey,
    int Score,
    IReadOnlyList<string> Reasons);

public sealed record MatchResult<T>(
    MatchStatus Status,
    MatchCandidate<T>? Selected,
    IReadOnlyList<MatchCandidate<T>> Candidates)
{
    internal static MatchResult<T> FromCandidates(IEnumerable<MatchCandidate<T>> candidates)
    {
        MatchCandidate<T>[] ordered = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0)
        {
            return new(MatchStatus.Unmatched, null, []);
        }

        int bestScore = ordered[0].Score;
        int bestCount = ordered.Count(candidate => candidate.Score == bestScore);
        if (bestCount > 1)
        {
            return new(MatchStatus.Ambiguous, null, ordered);
        }

        return new(MatchStatus.Matched, ordered[0], ordered);
    }
}
