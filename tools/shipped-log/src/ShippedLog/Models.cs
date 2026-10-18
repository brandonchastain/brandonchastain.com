namespace ShippedLog;

/// <summary>One commit on a repo's default branch, authored by the owner.</summary>
public sealed record Commit(
    string Sha,
    string Subject,
    string Body,
    string AuthorLogin,
    DateTimeOffset Date,
    int ParentCount);

public sealed record PullRequest(
    int Number,
    string Title,
    string Body,
    DateTimeOffset MergedAt,
    string Url);

public sealed record Release(
    string Tag,
    string Name,
    string Body,
    DateTimeOffset PublishedAt,
    string Url);

public sealed record Deployment(
    string Environment,
    string State,
    DateTimeOffset CreatedAt);

/// <summary>Everything collected for one repo in the week.</summary>
public sealed record RepoActivity(
    string Name,
    string Url,
    string DefaultBranch,
    List<Commit> Commits,
    List<PullRequest> MergedPullRequests,
    List<Release> Releases,
    List<Deployment> Deployments)
{
    public bool HasShippedWork =>
        Commits.Count > 0 || MergedPullRequests.Count > 0 || Releases.Count > 0;
}

/// <summary>The window a run covers, inclusive start, exclusive end.</summary>
public sealed record WeekWindow(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>
    /// ISO 8601 week id for the window, e.g. 2026-W42. Taken from the window's midpoint so a run that
    /// fires just after midnight UTC on Monday (Sunday evening Pacific) is still labeled with the week it covers.
    /// </summary>
    public string IsoWeekId
    {
        get
        {
            var mid = (Start + (End - Start) / 2).UtcDateTime;
            var year = System.Globalization.ISOWeek.GetYear(mid);
            var week = System.Globalization.ISOWeek.GetWeekOfYear(mid);
            return $"{year}-W{week:00}";
        }
    }

    public static WeekWindow EndingAt(DateTimeOffset end) => new(end.AddDays(-7), end);
}

/// <summary>Stats line shown under the heading.</summary>
public sealed record WeekStats(int ActiveDays, int ReposTouched, int PullRequestsMerged, int Commits)
{
    public static WeekStats From(IEnumerable<RepoActivity> repos)
    {
        var active = repos.Where(r => r.HasShippedWork).ToList();
        var days = active
            .SelectMany(r => r.Commits.Select(c => c.Date.UtcDateTime.Date)
                .Concat(r.MergedPullRequests.Select(p => p.MergedAt.UtcDateTime.Date)))
            .Distinct()
            .Count();
        return new WeekStats(
            days,
            active.Count,
            active.Sum(r => r.MergedPullRequests.Count),
            active.Sum(r => r.Commits.Count));
    }
}
