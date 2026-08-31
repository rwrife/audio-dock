using AudioDock.Core.Matching;
using AudioDock.Core.Models;

namespace AudioDock.Core.Planning;

public static class ScenePlanner
{
    public static ScenePlan Plan(AudioScene scene, AudioSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (scene.SchemaVersion != AudioScene.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Scene schema {scene.SchemaVersion} is not supported by this planner.");
        }

        var changes = new List<PlannedChange>();
        int sequence = 0;

        void Add(
            ChangeKind kind,
            PlanDisposition disposition,
            string target,
            string? targetId,
            string? before,
            string? after,
            string explanation,
            EndpointMatchRule? endpointMatch = null,
            ApplicationMatchRule? applicationMatch = null,
            AudioDirection? direction = null,
            AudioRole? role = null,
            VolumeLevel? volume = null,
            bool? isMuted = null) =>
            changes.Add(new(++sequence, kind, disposition, target, targetId, before, after, explanation,
                endpointMatch, applicationMatch, direction, role, volume, isMuted));

        foreach (RoleTarget target in scene.RoleTargets.OrderBy(item => item.Endpoint.Direction).ThenBy(item => item.Role))
        {
            MatchResult<EndpointDescriptor> match = EndpointMatcher.Match(target.Endpoint, snapshot.Endpoints);
            string label = $"{target.Endpoint.Direction} {target.Role} role";
            if (match.Status != MatchStatus.Matched)
            {
                Add(ChangeKind.DefaultRole, PlanDisposition.Skipped, label, null, null, null, Explain(match.Status, "endpoint"),
                    endpointMatch: target.Endpoint, direction: target.Endpoint.Direction, role: target.Role);
                continue;
            }

            EndpointDescriptor endpoint = match.Selected!.Value;
            EndpointDescriptor? current = snapshot.Endpoints.FirstOrDefault(candidate =>
                candidate.Direction == target.Endpoint.Direction && candidate.DefaultRoles.Contains(target.Role));

            if (endpoint.DefaultRoles.Contains(target.Role))
            {
                Add(ChangeKind.DefaultRole, PlanDisposition.NoOp, label, endpoint.StableId, endpoint.StableId, endpoint.StableId, "The endpoint already owns this role.",
                    endpointMatch: target.Endpoint, direction: target.Endpoint.Direction, role: target.Role);
            }
            else if (!endpoint.Capabilities.CanSetDefaultRole)
            {
                Add(ChangeKind.DefaultRole, PlanDisposition.Skipped, label, endpoint.StableId, current?.StableId, endpoint.StableId, "The adapter did not report role-selection capability for this endpoint.",
                    endpointMatch: target.Endpoint, direction: target.Endpoint.Direction, role: target.Role);
            }
            else
            {
                Add(ChangeKind.DefaultRole, PlanDisposition.Apply, label, endpoint.StableId, current?.StableId ?? "none", endpoint.StableId, "Set the role only after a later executor re-resolves the endpoint.",
                    endpointMatch: target.Endpoint, direction: target.Endpoint.Direction, role: target.Role);
            }
        }

        foreach (EndpointRule rule in scene.EndpointRules)
        {
            MatchResult<EndpointDescriptor> match = EndpointMatcher.Match(rule.Match, snapshot.Endpoints);
            string label = Describe(rule.Match);
            if (match.Status != MatchStatus.Matched)
            {
                string reason = Explain(match.Status, "endpoint");
                if (rule.Volume is not null)
                {
                    Add(ChangeKind.EndpointVolume, PlanDisposition.Skipped, label, null, null, rule.Volume.ToString(), reason,
                        endpointMatch: rule.Match, volume: rule.Volume);
                }

                if (rule.IsMuted is not null)
                {
                    Add(ChangeKind.EndpointMute, PlanDisposition.Skipped, label, null, null, Format(rule.IsMuted.Value), reason,
                        endpointMatch: rule.Match, isMuted: rule.IsMuted);
                }

                continue;
            }

            EndpointDescriptor endpoint = match.Selected!.Value;
            if (rule.Volume is VolumeLevel desiredVolume)
            {
                if (!endpoint.Capabilities.CanSetVolume)
                {
                    Add(ChangeKind.EndpointVolume, PlanDisposition.Skipped, label, endpoint.StableId, endpoint.Volume?.ToString(), desiredVolume.ToString(), "Endpoint volume control is unavailable.",
                        endpointMatch: rule.Match, volume: desiredVolume);
                }
                else if (endpoint.Volume == desiredVolume)
                {
                    Add(ChangeKind.EndpointVolume, PlanDisposition.NoOp, label, endpoint.StableId, desiredVolume.ToString(), desiredVolume.ToString(), "Endpoint volume already matches.",
                        endpointMatch: rule.Match, volume: desiredVolume);
                }
                else
                {
                    Add(ChangeKind.EndpointVolume, PlanDisposition.Apply, label, endpoint.StableId, endpoint.Volume?.ToString() ?? "unknown", desiredVolume.ToString(), "Set bounded endpoint volume after re-resolution.",
                        endpointMatch: rule.Match, volume: desiredVolume);
                }
            }

            if (rule.IsMuted is bool desiredMute)
            {
                if (!endpoint.Capabilities.CanSetMute)
                {
                    Add(ChangeKind.EndpointMute, PlanDisposition.Skipped, label, endpoint.StableId, Format(endpoint.IsMuted), Format(desiredMute), "Endpoint mute control is unavailable.",
                        endpointMatch: rule.Match, isMuted: desiredMute);
                }
                else if (endpoint.IsMuted == desiredMute)
                {
                    Add(ChangeKind.EndpointMute, PlanDisposition.NoOp, label, endpoint.StableId, Format(desiredMute), Format(desiredMute), "Endpoint mute already matches.",
                        endpointMatch: rule.Match, isMuted: desiredMute);
                }
                else
                {
                    Add(ChangeKind.EndpointMute, PlanDisposition.Apply, label, endpoint.StableId, Format(endpoint.IsMuted), Format(desiredMute), "Set endpoint mute after re-resolution.",
                        endpointMatch: rule.Match, isMuted: desiredMute);
                }
            }
        }

        foreach (ApplicationRule rule in scene.ApplicationRules)
        {
            MatchResult<SessionDescriptor> match = ApplicationMatcher.Match(rule.Match, snapshot.Sessions);
            string label = Describe(rule.Match);
            if (match.Status != MatchStatus.Matched)
            {
                string reason = match.Status == MatchStatus.Unmatched
                    ? "No running session matched; the deferred rule is not reported as applied."
                    : Explain(match.Status, "application session");
                if (rule.Volume is not null)
                {
                    Add(ChangeKind.SessionVolume, PlanDisposition.Skipped, label, null, null, rule.Volume.ToString(), reason,
                        applicationMatch: rule.Match, volume: rule.Volume);
                }

                if (rule.IsMuted is not null)
                {
                    Add(ChangeKind.SessionMute, PlanDisposition.Skipped, label, null, null, Format(rule.IsMuted.Value), reason,
                        applicationMatch: rule.Match, isMuted: rule.IsMuted);
                }

                continue;
            }

            SessionDescriptor session = match.Selected!.Value;
            if (rule.Volume is VolumeLevel desiredVolume)
            {
                if (!session.Capabilities.CanSetVolume)
                {
                    Add(ChangeKind.SessionVolume, PlanDisposition.Skipped, label, session.SessionId, session.Volume?.ToString(), desiredVolume.ToString(), "Session volume control is unavailable.",
                        applicationMatch: rule.Match, volume: desiredVolume);
                }
                else if (session.Volume == desiredVolume)
                {
                    Add(ChangeKind.SessionVolume, PlanDisposition.NoOp, label, session.SessionId, desiredVolume.ToString(), desiredVolume.ToString(), "Session volume already matches.",
                        applicationMatch: rule.Match, volume: desiredVolume);
                }
                else
                {
                    Add(ChangeKind.SessionVolume, PlanDisposition.Apply, label, session.SessionId, session.Volume?.ToString() ?? "unknown", desiredVolume.ToString(), "Set bounded session volume after re-resolution.",
                        applicationMatch: rule.Match, volume: desiredVolume);
                }
            }

            if (rule.IsMuted is bool desiredMute)
            {
                if (!session.Capabilities.CanSetMute)
                {
                    Add(ChangeKind.SessionMute, PlanDisposition.Skipped, label, session.SessionId, Format(session.IsMuted), Format(desiredMute), "Session mute control is unavailable.",
                        applicationMatch: rule.Match, isMuted: desiredMute);
                }
                else if (session.IsMuted == desiredMute)
                {
                    Add(ChangeKind.SessionMute, PlanDisposition.NoOp, label, session.SessionId, Format(desiredMute), Format(desiredMute), "Session mute already matches.",
                        applicationMatch: rule.Match, isMuted: desiredMute);
                }
                else
                {
                    Add(ChangeKind.SessionMute, PlanDisposition.Apply, label, session.SessionId, Format(session.IsMuted), Format(desiredMute), "Set session mute after re-resolution.",
                        applicationMatch: rule.Match, isMuted: desiredMute);
                }
            }
        }

        return new ScenePlan(scene.Id, changes);
    }

    private static string Describe(EndpointMatchRule rule) =>
        rule.UserAlias ?? rule.FriendlyName ?? rule.Product ?? rule.ExactId ?? $"{rule.Direction} endpoint";

    private static string Describe(ApplicationMatchRule rule) =>
        rule.UserAlias ?? rule.ProductName ?? rule.ProcessName ?? rule.PackageFamilyName ??
        (rule.ExecutablePath is null ? "application session" : "path-based application rule");

    private static string Explain(MatchStatus status, string target) => status switch
    {
        MatchStatus.Unmatched => $"No {target} matched the rule.",
        MatchStatus.Ambiguous => $"Multiple {target}s share the best score; user resolution is required.",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string Format(bool? value) => value switch
    {
        true => "muted",
        false => "unmuted",
        null => "unknown",
    };
}
