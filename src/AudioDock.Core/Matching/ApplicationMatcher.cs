using AudioDock.Core.Models;

namespace AudioDock.Core.Matching;

public static class ApplicationMatcher
{
    private const int PackageWeight = 1_000_000_000;
    private const int PathWeight = 100_000_000;
    private const int AliasWeight = 10_000_000;
    private const int PublisherWeight = 1_000_000;
    private const int ProductWeight = 1_000_000;
    private const int ProcessNameWeight = 100_000;

    public static MatchResult<SessionDescriptor> Match(
        ApplicationMatchRule rule,
        IEnumerable<SessionDescriptor> sessions)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sessions);

        var candidates = new List<MatchCandidate<SessionDescriptor>>();
        foreach (SessionDescriptor session in sessions)
        {
            if (session.State != SessionState.Active)
            {
                continue;
            }

            int score = 0;
            var reasons = new List<string>();
            AddIfEqual(rule.PackageFamilyName, session.PackageFamilyName, PackageWeight, "package family", ref score, reasons);
            AddIfEqual(rule.ExecutablePath, session.ExecutablePath, PathWeight, "user-approved executable path", ref score, reasons);
            AddIfAlias(rule.UserAlias, session.Aliases, AliasWeight, ref score, reasons);
            AddPortableFallback(rule, session, ref score, reasons);

            if (score > 0)
            {
                candidates.Add(new(session, session.SessionId, score, reasons));
            }
        }

        return MatchResult<SessionDescriptor>.FromCandidates(candidates);
    }

    private static void AddPortableFallback(
        ApplicationMatchRule rule,
        SessionDescriptor session,
        ref int score,
        List<string> reasons)
    {
        bool hasPublisher = !string.IsNullOrWhiteSpace(rule.Publisher);
        bool hasProduct = !string.IsNullOrWhiteSpace(rule.ProductName);
        bool hasProcessName = !string.IsNullOrWhiteSpace(rule.ProcessName);
        bool hasProductIdentity = hasPublisher || hasProduct;
        if (hasProductIdentity &&
            ((hasPublisher && !Same(rule.Publisher!, session.Publisher)) ||
             (hasProduct && !Same(rule.ProductName!, session.ProductName)) ||
             (hasProcessName && !Same(rule.ProcessName!, session.ProcessName))))
        {
            return;
        }

        if (hasPublisher)
        {
            score += PublisherWeight;
            reasons.Add("publisher");
        }

        if (hasProduct)
        {
            score += ProductWeight;
            reasons.Add("product name");
        }

        if (hasProcessName && Same(rule.ProcessName!, session.ProcessName))
        {
            score += ProcessNameWeight;
            reasons.Add("process name");
        }
    }

    private static void AddIfAlias(
        string? expected,
        IReadOnlyCollection<string>? aliases,
        int weight,
        ref int score,
        List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(expected) || aliases is null ||
            !aliases.Any(alias => Same(expected, alias)))
        {
            return;
        }

        score += weight;
        reasons.Add("user-approved alias");
    }

    private static void AddIfEqual(
        string? expected,
        string? actual,
        int weight,
        string reason,
        ref int score,
        List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(expected) || !Same(expected, actual))
        {
            return;
        }

        score += weight;
        reasons.Add(reason);
    }

    private static bool Same(string expected, string? actual) =>
        !string.IsNullOrWhiteSpace(actual) &&
        string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
}
