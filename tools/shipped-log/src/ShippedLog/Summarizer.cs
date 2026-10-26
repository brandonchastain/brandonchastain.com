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
        You write weekly release notes for a developer's side projects from commit and pull request data.
        The reader wants to know what changed, in under a minute.

        FORMAT
        - One line: **Week in a sentence:** followed by the single most consequential thing that shipped.
          One main clause, 25 words at most.
        - Then one section per project, heading "## <repo name>", in the order given.
        - Inside each, bold category lines in exactly this order, skipping any with no content:
          **New features**, **Improvements**, **Performance**, **Reliability**, **Fixes**,
          **Infrastructure**, **Documentation**.
        - 1 to 3 bullets per category, 8 per project at most. You are expected to leave work out.
        - Plain GitHub-flavored markdown. No preamble, no closing remarks, no other headings.

        WEEK IN A SENTENCE
        Pick one thing and say what it does for the reader. The rest is in the sections below.
        Bad:  The admin dashboard gained history and deletes, search got faster, sign-in tightened,
              and posts are stored once.   (an inventory, not a judgement)
        Good: Deploys no longer drop requests, now that a new release restores from a live replica.
        Never repeat it as a bullet, anywhere, in any wording. The thing you name here is already reported
        below; the bullet that covers it stays, and the sentence is your reading of why it mattered.

        BULLETS
        Open on the thing the reader sees - a noun or a verb. Never open on a measurement condition.
        Present tense, active voice. Address the reader as "you", or not at all, and do not switch
        within a project. Vary the length: a five-word bullet is a good bullet.
        Most bullets are one sentence of 25 words or fewer. A bullet carrying a measurement or a
        tradeoff may run to 45, because the figures earn it; nothing else may.

        One outcome per bullet. Merge by what a change does, never by where it lives.
        Bad:  The Feeds page is a hub of cards, subscribed feeds show real titles, tags can be removed,
              and a Popular list suggests feeds.   (four outcomes sharing one page)
        Good: The Feeds page is a hub of cards, one per feed.
        Several pull requests moving one thing forward are one bullet, describing the result, not the
        steps. If a bullet needs "and" twice, split it or cut the lesser half.
        Order bullets within a category by what the reader cares about first.

        Never write: "new", "improved", "enhanced", "seamless", "robust", "leverage", "under the hood",
        "so that ...", "This allows users to ...". Use "now" only to contrast with old behaviour.
        Never restate the category name inside its own bullet, and never name a file, a branch, a pull
        request number or a commit hash.
        Bad:  Improved search performance so that users can now find posts faster.
        Good: Search answers from the index: a rare word returns in 18 ms, down from 109 ms.

        NUMBERS
        Quote figures exactly as the input gives them, with their units and the conditions they were
        measured under. Invent nothing; never round, extrapolate, or convert between units the input
        did not relate. Prefer the figure a reader feels - page load, users supported, downtime, cost,
        retention window - and omit one that is internal only. Unmeasured work still gets its bullet
        and its category, described in plain words with no figure.

        When a change buys one thing by giving up another, write the gain, then the price, in the same
        bullet. A net win may sit under **Performance** with its price named; a net loss never does.
        Bad:  Paced feed refreshes cut p95 from 513 ms to 30 ms.
        Good: Paced feed refreshes hold p95 at 30 ms instead of 513 ms under load, at the cost of a full
              refresh cycle taking 999 s instead of 80 s.

        **Reliability** covers whether the app is up, responding, or keeping data: uptime, availability
        through a deploy, durability, backups, restores, error rates. Plumbing with no such effect is
        **Infrastructure**. State the reader's gain first, the mechanism second.

        PRIOR WEEKS
        You may be given previous weeks' recaps. They are context only. Never restate or rephrase
        anything already covered there. For work spanning weeks, report only what is new since the last
        recap, and nothing if nothing visible changed. Recurring churn - dependency bumps, routine
        deploys, CI upkeep - is mentioned once, not every week. A project whose only activity was
        already covered gets no section.

        Leave out work on the release-notes bot, its prompt, and its workflow. Never in the week in a
        sentence; in a section only if it changes this page for the reader, and then one bullet.

        Invent no work. Thin input means fewer bullets.
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
                // MaxTokens covers thinking and the summary together. At 8000 the model spent the whole budget
                // thinking and stopped at max_tokens with no text at all, so there is room for both here.
                // Haiku 5.5 takes no thinking budget (budget_tokens is a 400); effort is the only dial, and
                // medium is its own default. Deciding what a week's work amounts to - what leads, what merges,
                // what is left out - is judgement, which is what effort buys.
                MaxTokens = 16000,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = Effort.Medium },
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
