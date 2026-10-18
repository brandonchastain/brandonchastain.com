using System.Text.Json;
using ShippedLog;

namespace ShippedLog.Tests;

public class NoiseFilterTests
{
    private static Commit C(string subject, string author = "brandonchastain", int parents = 1) =>
        new("deadbeef", subject, "", author, DateTimeOffset.UtcNow, parents);

    [Theory]
    [InlineData("Merge branch 'main' into feature")]
    [InlineData("Merge pull request #12 from x/y")]
    [InlineData("Merge remote-tracking branch 'origin/main'")]
    public void MergeSubjectsAreNoise(string subject) => Assert.True(NoiseFilter.IsNoise(C(subject)));

    [Fact]
    public void TwoParentCommitIsNoiseRegardlessOfSubject() =>
        Assert.True(NoiseFilter.IsNoise(C("Ship the thing", parents: 2)));

    [Theory]
    [InlineData("Bump Newtonsoft.Json from 13.0.1 to 13.0.3")]
    [InlineData("chore(deps): update dependency xunit to v2.9")]
    [InlineData("chore(deps-dev): bump eslint")]
    [InlineData("Update dependency typescript to v5")]
    public void DependencyBumpsAreNoise(string subject) => Assert.True(NoiseFilter.IsDependencyBump(subject));

    [Theory]
    [InlineData("dependabot[bot]")]
    [InlineData("renovate[bot]")]
    [InlineData("some-other[bot]")]
    public void BotAuthorsAreNoise(string login) => Assert.True(NoiseFilter.IsNoise(C("Real looking subject", login)));

    [Theory]
    [InlineData("dotnet format")]
    [InlineData("Formatting")]
    [InlineData("style: prettier")]
    [InlineData("Run prettier")]
    [InlineData("whitespace")]
    [InlineData("fix indentation")]
    public void FormattingOnlySubjectsAreNoise(string subject) => Assert.True(NoiseFilter.IsFormattingOnly(subject));

    [Theory]
    [InlineData("Fix formatting of dates in the export view")]
    [InlineData("Add AI tag suggestions with confirm/reject")]
    [InlineData("Update README with setup steps")]
    [InlineData("Bump version to 1.2.0 for release")]
    public void RealWorkIsKept(string subject) => Assert.False(NoiseFilter.IsNoise(C(subject)));

    [Fact]
    public void SquashMergeCommitOfListedPrIsDropped()
    {
        var pr = new PullRequest(42, "Relevance scoring", "", DateTimeOffset.UtcNow, "u");
        var repo = new RepoActivity("o/r", "u", "main",
            [C("feat: relevance scoring (#42)"), C("fix: typo (#99)"), C("chore: no pr ref")],
            [pr], [], []);

        var filtered = NoiseFilter.Apply(repo);

        Assert.Equal(["fix: typo (#99)", "chore: no pr ref"], filtered.Commits.Select(c => c.Subject));
        Assert.Single(filtered.MergedPullRequests);
    }

    [Fact]
    public void ApplyFiltersFixtureAndGroupsByRepo()
    {
        var repos = LoadFixture().Select(NoiseFilter.Apply).Where(r => r.HasShippedWork).OrderBy(r => r.Name).ToList();

        Assert.Equal(2, repos.Count);
        Assert.Equal(["example/alpha", "example/beta"], repos.Select(r => r.Name));

        var rss = repos[0];
        Assert.Equal(["Add AI tag suggestions with confirm/reject", "Fix formatting of dates in the export view"],
            rss.Commits.Select(c => c.Subject));
        Assert.Single(rss.MergedPullRequests);
        Assert.Equal(42, rss.MergedPullRequests[0].Number);
        Assert.Single(rss.Deployments);

        Assert.Single(repos[1].Releases);
    }

    [Fact]
    public void FixtureEndToEndRendersFallbackMarkdown()
    {
        var window = WeekWindow.EndingAt(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        var repos = LoadFixture().Select(NoiseFilter.Apply).Where(r => r.HasShippedWork).OrderBy(r => r.Name).ToList();
        var summary = SummaryInput.Fallback(repos);
        var md = MarkdownWriter.RenderWeek(window, repos, summary);

        Assert.StartsWith("# 2026-W42", md);
        Assert.Contains("## example/alpha", md);
        Assert.Contains("## example/beta", md);
        Assert.Contains("PR #42", md);
        Assert.DoesNotContain("Bump Microsoft.Data.Sqlite", md);
        Assert.DoesNotContain("quiet-repo", md);
        Assert.Equal("3 commits and 1 merged PR across 2 repos.", MarkdownWriter.OneLiner(summary));
    }

    internal static List<RepoActivity> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-week.json");
        return JsonSerializer.Deserialize<List<RepoActivity>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
