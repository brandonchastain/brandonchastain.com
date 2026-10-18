using System.Text.RegularExpressions;

namespace ShippedLog;

/// <summary>
/// Drops the commits and PRs that are not "shipped work": merge commits,
/// dependency bumps from bots, and formatting-only changes.
/// </summary>
public static partial class NoiseFilter
{
    private static readonly string[] BotLogins =
    [
        "dependabot[bot]", "dependabot", "renovate[bot]", "renovate", "renovate-bot",
        "github-actions[bot]", "greenkeeper[bot]", "snyk-bot",
    ];

    // "Bump foo from 1.2 to 1.3", "Update dependency x to v2", "chore(deps): ..."
    [GeneratedRegex(@"^(chore)?\(?deps?(-dev)?\)?:?\s*|^bump\s+\S+\s+from\s+\S+\s+to\s+\S+|^update\s+(dependency|dependencies)\b|^upgrade\s+\S+\s+to\s+v?\d",
        RegexOptions.IgnoreCase)]
    private static partial Regex DependencyBump();

    // Formatting-only subjects. Anchored so "fix formatting of dates in export" is kept.
    [GeneratedRegex(@"^(style|chore)?\(?[\w-]*\)?:?\s*(run\s+)?(dotnet\s+format|prettier|eslint\s+--fix|format(ting)?|reformat|lint(ing)?\s+fixes?|whitespace|fix\s+indentation|apply\s+formatting|code\s+style)\s*(only|fixes?|changes?)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FormattingOnly();

    [GeneratedRegex(@"^Merge\s+(branch|pull request|remote-tracking branch)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MergeSubject();

    public static bool IsBot(string login) =>
        BotLogins.Contains(login, StringComparer.OrdinalIgnoreCase)
        || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

    public static bool IsMergeCommit(Commit c) =>
        c.ParentCount > 1 || MergeSubject().IsMatch(c.Subject);

    public static bool IsDependencyBump(string subject) => DependencyBump().IsMatch(subject);

    public static bool IsFormattingOnly(string subject) => FormattingOnly().IsMatch(subject.Trim());

    public static bool IsNoise(Commit c) =>
        IsMergeCommit(c) || IsBot(c.AuthorLogin) || IsDependencyBump(c.Subject) || IsFormattingOnly(c.Subject);

    public static bool IsNoise(PullRequest pr) =>
        IsDependencyBump(pr.Title) || IsFormattingOnly(pr.Title);

    // Squash/rebase merges put "(#123)" at the end of the commit subject.
    [GeneratedRegex(@"\(#(\d+)\)\s*$")]
    private static partial Regex TrailingPrNumber();

    /// <summary>A commit that is the squash-merge of a PR already in the list adds nothing.</summary>
    public static bool DuplicatesPullRequest(Commit c, IEnumerable<PullRequest> prs)
    {
        var m = TrailingPrNumber().Match(c.Subject);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && prs.Any(p => p.Number == n);
    }

    public static RepoActivity Apply(RepoActivity repo)
    {
        var prs = repo.MergedPullRequests.Where(p => !IsNoise(p)).ToList();
        var commits = repo.Commits.Where(c => !IsNoise(c) && !DuplicatesPullRequest(c, prs)).ToList();
        return repo with { Commits = commits, MergedPullRequests = prs };
    }
}
