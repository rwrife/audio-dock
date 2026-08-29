using AudioDock.Core.Matching;
using AudioDock.Core.Models;

namespace AudioDock.Core.Tests.Matching;

public sealed class EndpointMatcherTests
{
    [Fact]
    public void ExactIdOutranksAliasFallback()
    {
        EndpointDescriptor exact = TestData.Endpoint("exact");
        EndpointDescriptor alias = TestData.Endpoint(
            "alias",
            name: "Speakers",
            interfaceId: "interface",
            containerId: "container",
            manufacturer: "Contoso",
            product: "Dock",
            aliases: ["Desk"]);
        var rule = new EndpointMatchRule(
            AudioDirection.Render,
            ExactId: "exact",
            UserAlias: "Desk",
            InterfaceId: "interface",
            ContainerId: "container",
            FriendlyName: "Speakers",
            Manufacturer: "Contoso",
            Product: "Dock");

        MatchResult<EndpointDescriptor> result = EndpointMatcher.Match(rule, [alias, exact]);

        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal("exact", result.Selected!.Value.StableId);
        Assert.Contains("exact endpoint id", result.Selected.Reasons);
    }

    [Fact]
    public void UserApprovedAliasMatchesWithoutPathOrId()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("usb", aliases: ["My dock"]);

        MatchResult<EndpointDescriptor> result = EndpointMatcher.Match(
            new EndpointMatchRule(AudioDirection.Render, UserAlias: "my DOCK"),
            [endpoint]);

        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal("usb", result.Selected!.Value.StableId);
    }

    [Fact]
    public void FriendlyNameAndManufacturerProvideDeterministicFallback()
    {
        EndpointDescriptor generic = TestData.Endpoint("b", name: "Speakers", manufacturer: "Other");
        EndpointDescriptor preferred = TestData.Endpoint("a", name: "Speakers", manufacturer: "Contoso");
        var rule = new EndpointMatchRule(
            AudioDirection.Render,
            FriendlyName: "Speakers",
            Manufacturer: "Contoso");

        MatchResult<EndpointDescriptor> result = EndpointMatcher.Match(rule, [generic, preferred]);

        Assert.Equal("a", result.Selected!.Value.StableId);
        Assert.Contains("manufacturer", result.Selected.Reasons);
    }

    [Fact]
    public void EqualBestScoresAreAmbiguousAndSortedByStableId()
    {
        EndpointDescriptor second = TestData.Endpoint("z", name: "USB Audio");
        EndpointDescriptor first = TestData.Endpoint("a", name: "USB Audio");

        MatchResult<EndpointDescriptor> result = EndpointMatcher.Match(
            new EndpointMatchRule(AudioDirection.Render, FriendlyName: "USB Audio"),
            [second, first]);

        Assert.Equal(MatchStatus.Ambiguous, result.Status);
        Assert.Null(result.Selected);
        Assert.Equal(["a", "z"], result.Candidates.Select(candidate => candidate.StableKey));
    }

    [Fact]
    public void PartialPortableFallbackDoesNotMatch()
    {
        EndpointDescriptor endpoint = TestData.Endpoint("wrong", name: "Headphones", manufacturer: "Contoso");
        var rule = new EndpointMatchRule(
            AudioDirection.Render,
            FriendlyName: "Speakers",
            Manufacturer: "Contoso");

        MatchResult<EndpointDescriptor> result = EndpointMatcher.Match(rule, [endpoint]);

        Assert.Equal(MatchStatus.Unmatched, result.Status);
    }

    [Fact]
    public void InactiveEndpointDoesNotMatchEvenByExactId()
    {
        EndpointDescriptor unplugged = TestData.Endpoint("usb", state: EndpointState.Unplugged);

        MatchResult<EndpointDescriptor> result = EndpointMatcher.Match(
            new EndpointMatchRule(AudioDirection.Render, ExactId: "usb"),
            [unplugged]);

        Assert.Equal(MatchStatus.Unmatched, result.Status);
    }

    [Fact]
    public void DirectionOnlyOrWrongDirectionNeverSilentlyMatches()
    {
        EndpointDescriptor capture = TestData.Endpoint("mic", direction: AudioDirection.Capture);

        MatchResult<EndpointDescriptor> result = EndpointMatcher.Match(
            new EndpointMatchRule(AudioDirection.Render, FriendlyName: "Speakers"),
            [capture]);

        Assert.Equal(MatchStatus.Unmatched, result.Status);
        Assert.Empty(result.Candidates);
    }
}
