using AudioDock.Core.Matching;
using AudioDock.Core.Models;

namespace AudioDock.Core.Tests.Matching;

public sealed class ApplicationMatcherTests
{
    [Fact]
    public void PackageIdentityOutranksProductFallback()
    {
        SessionDescriptor package = TestData.Session("package", packageFamily: "Contoso.Meet_abc", product: "Meet");
        SessionDescriptor product = TestData.Session(
            "product",
            product: "Meet",
            publisher: "Contoso",
            path: @"C:\Apps\Meet.exe",
            aliases: ["Calls"]);
        var rule = new ApplicationMatchRule(
            PackageFamilyName: "Contoso.Meet_abc",
            ExecutablePath: @"C:\Apps\Meet.exe",
            UserAlias: "Calls",
            Publisher: "Contoso",
            ProductName: "Meet");

        MatchResult<SessionDescriptor> result = ApplicationMatcher.Match(rule, [product, package]);

        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal("package", result.Selected!.Value.SessionId);
    }

    [Fact]
    public void ExecutablePathIsUsedOnlyWhenRuleExplicitlyContainsIt()
    {
        SessionDescriptor session = TestData.Session("classic", path: @"C:\Apps\Meet.exe");

        MatchResult<SessionDescriptor> withoutPath = ApplicationMatcher.Match(new ApplicationMatchRule(), [session]);
        MatchResult<SessionDescriptor> withPath = ApplicationMatcher.Match(
            new ApplicationMatchRule(ExecutablePath: @"c:\apps\MEET.exe"),
            [session]);

        Assert.Equal(MatchStatus.Unmatched, withoutPath.Status);
        Assert.Equal(MatchStatus.Matched, withPath.Status);
    }

    [Fact]
    public void PublisherAndProductProvidePortableFallback()
    {
        SessionDescriptor session = TestData.Session("meet", publisher: "Contoso", product: "Meet");

        MatchResult<SessionDescriptor> result = ApplicationMatcher.Match(
            new ApplicationMatchRule(Publisher: "contoso", ProductName: "meet"),
            [session]);

        Assert.Equal("meet", result.Selected!.Value.SessionId);
        Assert.Equal(2_000_000, result.Selected.Score);
    }

    [Fact]
    public void PartialPublisherProductFallbackDoesNotMatch()
    {
        SessionDescriptor session = TestData.Session("wrong", publisher: "Contoso", product: "Other");

        MatchResult<SessionDescriptor> result = ApplicationMatcher.Match(
            new ApplicationMatchRule(Publisher: "Contoso", ProductName: "Meet"),
            [session]);

        Assert.Equal(MatchStatus.Unmatched, result.Status);
    }

    [Fact]
    public void ExpiredSessionDoesNotMatchEvenByPackageIdentity()
    {
        SessionDescriptor expired = TestData.Session(
            "expired",
            packageFamily: "Contoso.Meet_abc",
            state: SessionState.Expired);

        MatchResult<SessionDescriptor> result = ApplicationMatcher.Match(
            new ApplicationMatchRule(PackageFamilyName: "Contoso.Meet_abc"),
            [expired]);

        Assert.Equal(MatchStatus.Unmatched, result.Status);
    }

    [Fact]
    public void EqualProcessNamesRemainAmbiguous()
    {
        SessionDescriptor first = TestData.Session("one", processName: "browser");
        SessionDescriptor second = TestData.Session("two", processName: "browser");

        MatchResult<SessionDescriptor> result = ApplicationMatcher.Match(
            new ApplicationMatchRule(ProcessName: "browser"),
            [first, second]);

        Assert.Equal(MatchStatus.Ambiguous, result.Status);
        Assert.Null(result.Selected);
    }
}
