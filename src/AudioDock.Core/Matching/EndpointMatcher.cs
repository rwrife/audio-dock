using AudioDock.Core.Models;

namespace AudioDock.Core.Matching;

public static class EndpointMatcher
{
    private const int ExactIdWeight = 1_000_000_000;
    private const int AliasWeight = 100_000_000;
    private const int InterfaceWeight = 10_000_000;
    private const int ContainerWeight = 1_000_000;
    private const int FriendlyNameWeight = 100_000;
    private const int ManufacturerWeight = 10_000;
    private const int ProductWeight = 10_000;

    public static MatchResult<EndpointDescriptor> Match(
        EndpointMatchRule rule,
        IEnumerable<EndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(endpoints);

        var candidates = new List<MatchCandidate<EndpointDescriptor>>();
        foreach (EndpointDescriptor endpoint in endpoints)
        {
            if (endpoint.Direction != rule.Direction || endpoint.State != EndpointState.Active)
            {
                continue;
            }

            int score = 0;
            var reasons = new List<string>();
            AddIfEqual(rule.ExactId, endpoint.StableId, ExactIdWeight, "exact endpoint id", ref score, reasons);
            AddIfAlias(rule.UserAlias, endpoint.Aliases, AliasWeight, ref score, reasons);
            AddIfEqual(rule.InterfaceId, endpoint.InterfaceId, InterfaceWeight, "interface id", ref score, reasons);
            AddIfEqual(rule.ContainerId, endpoint.ContainerId, ContainerWeight, "container id", ref score, reasons);
            AddFallback(rule, endpoint, ref score, reasons);

            if (score > 0)
            {
                candidates.Add(new(endpoint, endpoint.StableId, score, reasons));
            }
        }

        return MatchResult<EndpointDescriptor>.FromCandidates(candidates);
    }

    private static void AddFallback(
        EndpointMatchRule rule,
        EndpointDescriptor endpoint,
        ref int score,
        List<string> reasons)
    {
        bool hasFriendlyName = !string.IsNullOrWhiteSpace(rule.FriendlyName);
        bool hasManufacturer = !string.IsNullOrWhiteSpace(rule.Manufacturer);
        bool hasProduct = !string.IsNullOrWhiteSpace(rule.Product);
        bool hasPortableAnchor = hasFriendlyName || (hasManufacturer && hasProduct);
        if (!hasPortableAnchor ||
            (hasFriendlyName && !Same(rule.FriendlyName!, endpoint.FriendlyName)) ||
            (hasManufacturer && !Same(rule.Manufacturer!, endpoint.Manufacturer)) ||
            (hasProduct && !Same(rule.Product!, endpoint.Product)))
        {
            return;
        }

        if (hasFriendlyName)
        {
            score += FriendlyNameWeight;
            reasons.Add("friendly name");
        }

        if (hasManufacturer)
        {
            score += ManufacturerWeight;
            reasons.Add("manufacturer");
        }

        if (hasProduct)
        {
            score += ProductWeight;
            reasons.Add("product");
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
