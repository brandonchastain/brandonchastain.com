using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace ShippedLog;

public interface ISummarizer
{
    /// <summary>Returns markdown: first line is the "week in a sentence", then per-project sections.</summary>
    /// <param name="priorWeeks">Recent weeks' recaps, newest first, so the model does not repeat them.</param>
    Task<string> SummarizeAsync(
        IReadOnlyList<RepoActivity> repos,
        WeekWindow window,
        IReadOnlyList<PriorWeek> priorWeeks,
        CancellationToken ct);
}

/// <summary>An earlier week's recap, with the raw-activity block already stripped.</summary>
public sealed record PriorWeek(string WeekId, string Narrative);

/// <summary>Builds the model input and the deterministic fallback. Pure; unit-tested.</summary>
public static class SummaryInput
{
    /// <summary>How much of a pull request body reaches the model. Enough for a "Measured" table to survive.</summary>
    public const int PullRequestBodyChars = 2500;

    public const string SystemPrompt =
        """
        You write the weekly release notes for a developer's side projects, from raw commit and pull request data.
        You are writing for a reader who wants to know what changed, not a changelog of every pull request.

        Shape:
        - Start with exactly one line beginning "**Week in a sentence:**" that captures the week in one sentence.
        - Then one section per project, heading "## <repo name>", in the order given.
        - Inside a project section, group the work by category, each on its own bold line followed by its bullets:
          **New features**, **Improvements**, **Fixes**, **Performance**, **Reliability**, **Infrastructure**,
          **Documentation**.
          Use only the categories that have real content this week, in that order. Skip the rest; never write an empty one.
        - 1 to 4 bullets per category, and at most 8 bullets in a project section.
        - **Reliability** is for anything that changes whether the app is up, responding, or keeping people's data:
          availability through a deploy, uptime, data durability, backups and restores, error rates, recovery from a
          failure. Plumbing with no visible effect on any of those is **Infrastructure**. When a piece of
          infrastructure work does change one - replication that keeps the app serving through a deploy, a restore
          path that stops data loss - it belongs under **Reliability**, stated as what the user gets, with the
          mechanism second.

        Synthesis, not enumeration:
        - Do not write one bullet per pull request. Several pull requests that move the same thing forward are ONE bullet,
          describing the outcome the reader would notice ("search now matches titles and body text"), not the steps taken.
        - A bullet is about a change in the product, not about a file, a branch, or a review.
        - Do not cite pull request numbers or commit hashes; the full list is already linked below the summary.
        - If a project saw only small scattered work, say so in one bullet rather than listing it all.

        Lead with the impact:
        - A change that was measured is reported by its effect, not its mechanism. Pull request bodies often carry
          before/after tables and benchmark figures; when they do, put the numbers in the bullet.
          Write "the timeline now serves 798 requests a second where it managed 198, and p95 page load fell from
          2.6 s to 4 ms", not "added an index hint to the timeline query".
        - Prefer the number the reader feels - concurrent users supported, page load, downtime during a deploy,
          cost per month, time saved - over the internal one. Availability is an impact like any other: "the app
          now stays up through a deploy, which used to drop requests for one to two minutes" beats "handed the
          database over between revisions". When the input gives a limit that moved (a capacity ceiling, a quota, a retention
          window), say what it moved from and to.
        - Quote figures exactly as the input states them, with their units and their conditions ("on a copy of
          production with 151,000 posts"). Never invent, extrapolate or round a number the input does not give,
          and never convert between units the input did not relate. If a change was not measured, describe the
          outcome in words and give no figure.
        - A bullet with a real number in it is worth more than three without. If a category is crowded, keep the
          measured changes and fold the rest into one closing bullet.

        Do not repeat earlier weeks:
        - You may be given the previous weeks' recaps. They are context only: never restate, rephrase, or re-announce
          something already covered there, even if this week's input touches it again.
        - For work that spans weeks, report only what is new since the last recap ("the API login cutover is now live"),
          and say nothing if nothing visible changed this week.
        - Ongoing background churn - repeated dependency bumps, recurring deploys, routine test or CI upkeep - is worth
          mentioning once, when it starts or when it ends, not every week.
        - Fewer, non-repetitive bullets always beats a full but repetitive section. A project whose only activity this
          week was already described in an earlier week gets no section at all.

        - Never invent work that is not in the input. If a project's input is thin, write fewer bullets.
        - Plain GitHub-flavored markdown. No preamble, no closing remarks, no headings other than the project ones.
        """;

    public static string Build(IReadOnlyList<RepoActivity> repos, WeekWindow window, IReadOnlyList<PriorWeek> priorWeeks)
    {
        var sb = new StringBuilder();
        if (priorWeeks.Count > 0)
        {
            sb.AppendLine("Previously reported (context only - do not repeat any of this):");
            sb.AppendLine();
            foreach (var prior in priorWeeks)
            {
                sb.AppendLine($"<prior-week id=\"{prior.WeekId}\">");
                sb.AppendLine(prior.Narrative.Trim());
                sb.AppendLine("</prior-week>");
                sb.AppendLine();
            }
            sb.AppendLine("End of previous weeks. Everything below is this week's raw activity.");
            sb.AppendLine();
        }
        sb.AppendLine($"Week: {window.Start:yyyy-MM-dd} to {window.End.AddDays(-1):yyyy-MM-dd}");
        sb.AppendLine();
        foreach (var repo in repos.Where(r => r.HasShippedWork))
        {
            sb.AppendLine($"# {repo.Name}");
            if (repo.MergedPullRequests.Count > 0)
            {
                sb.AppendLine("Merged pull requests:");
                foreach (var pr in repo.MergedPullRequests)
                {
                    sb.AppendLine($"- #{pr.Number} {pr.Title}");
                    // Generous: the before/after tables that make a bullet worth reading often sit well past
                    // the first few hundred characters, under a "## Measured" or "## Why" heading.
                    var body = Truncate(pr.Body, PullRequestBodyChars);
                    if (body.Length > 0) sb.AppendLine("  " + body.Replace("\n", "\n  "));
                }
            }
            if (repo.Commits.Count > 0)
            {
                sb.AppendLine("Commits on default branch:");
                foreach (var c in repo.Commits.OrderBy(c => c.Date))
                {
                    sb.AppendLine($"- {c.Subject}");
                    var body = Truncate(c.Body, 300);
                    if (body.Length > 0) sb.AppendLine("  " + body.Replace("\n", "\n  "));
                }
            }
            if (repo.Releases.Count > 0)
            {
                sb.AppendLine("Releases:");
                foreach (var r in repo.Releases) sb.AppendLine($"- {r.Tag} {r.Name}");
            }
            if (repo.Deployments.Count > 0)
            {
                var envs = repo.Deployments.GroupBy(d => d.Environment).Select(g => $"{g.Key} x{g.Count()}");
                sb.AppendLine($"Deployments: {string.Join(", ", envs)}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Plain list of PR titles and commit subjects per repo, used when the model call fails.</summary>
    public static string Fallback(IReadOnlyList<RepoActivity> repos)
    {
        var active = repos.Where(r => r.HasShippedWork).ToList();
        var sb = new StringBuilder();
        var prs = active.Sum(r => r.MergedPullRequests.Count);
        var commits = active.Sum(r => r.Commits.Count);
        sb.AppendLine($"**Week in a sentence:** {commits} commit{Plural(commits)} and {prs} merged PR{Plural(prs)} across {active.Count} repo{Plural(active.Count)}.");
        sb.AppendLine();
        sb.AppendLine("_AI summary unavailable this week; the full list is under Raw activity below._");
        sb.AppendLine();
        foreach (var repo in active)
        {
            var parts = new List<string>();
            if (repo.MergedPullRequests.Count > 0) parts.Add($"{repo.MergedPullRequests.Count} PR{Plural(repo.MergedPullRequests.Count)} merged");
            if (repo.Commits.Count > 0) parts.Add($"{repo.Commits.Count} commit{Plural(repo.Commits.Count)}");
            if (repo.Releases.Count > 0) parts.Add($"release{Plural(repo.Releases.Count)} {string.Join(", ", repo.Releases.Select(r => r.Tag))}");
            sb.AppendLine($"- **{repo.Name}**: {string.Join(", ", parts)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static string Truncate(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}

/// <summary>Calls Claude Haiku through the official Anthropic SDK; falls back to the plain list on any failure.</summary>
public sealed class ClaudeSummarizer : ISummarizer
{
    public const string DefaultModel = "claude-haiku-5-5";
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly TextWriter _log;

    public ClaudeSummarizer(string apiKey, TextWriter log, string? model = null)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
        _model = model ?? DefaultModel;
        _log = log;
    }

    public async Task<string> SummarizeAsync(
        IReadOnlyList<RepoActivity> repos,
        WeekWindow window,
        IReadOnlyList<PriorWeek> priorWeeks,
        CancellationToken ct)
    {
        var input = SummaryInput.Build(repos, window, priorWeeks);
        _log.WriteLine($"Summarizing with {_model}: {input.Length} chars in, {priorWeeks.Count} prior week(s).");
        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 8000,
                System = SummaryInput.SystemPrompt,
                Messages = [new() { Role = Role.User, Content = input }],
            }, cancellationToken: ct);

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
            if (text.Length == 0)
            {
                var blocks = string.Join(", ", response.Content.Select(b => b.Value?.GetType().Name ?? "null"));
                throw new InvalidOperationException(
                    $"Model returned no text (stop reason: {response.StopReason}; blocks: [{blocks}]).");
            }
            if (!text.Contains("Week in a sentence", StringComparison.OrdinalIgnoreCase))
                text = "**Week in a sentence:** " + text;
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.WriteLine($"warn: summarization failed ({ex.GetType().Name}: {ex.Message}); using fallback list.");
            return SummaryInput.Fallback(repos);
        }
    }
}

/// <summary>Used by --no-ai and tests.</summary>
public sealed class FallbackSummarizer : ISummarizer
{
    public Task<string> SummarizeAsync(
        IReadOnlyList<RepoActivity> repos,
        WeekWindow window,
        IReadOnlyList<PriorWeek> priorWeeks,
        CancellationToken ct) =>
        Task.FromResult(SummaryInput.Fallback(repos));
}
