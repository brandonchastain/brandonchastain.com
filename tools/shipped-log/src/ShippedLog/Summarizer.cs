using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace ShippedLog;

public interface ISummarizer
{
    /// <summary>Returns markdown: first line is the "week in a sentence", then per-project sections.</summary>
    Task<string> SummarizeAsync(IReadOnlyList<RepoActivity> repos, WeekWindow window, CancellationToken ct);
}

/// <summary>Builds the model input and the deterministic fallback. Pure; unit-tested.</summary>
public static class SummaryInput
{
    public const string SystemPrompt =
        "You write a short weekly recap of a developer's side-project work from raw commit and pull request data.\n\n" +
        "Rules:\n" +
        "- Start with exactly one line beginning \"**Week in a sentence:**\" that captures the week in one sentence.\n" +
        "- Then one section per project, heading \"## <repo name>\", in the order given.\n" +
        "- Under each heading, 2 to 5 bullets. Describe outcomes a user or the developer would notice " +
        "(for example \"added AI tag suggestions with confirm/reject\"), not file-level changes.\n" +
        "- Merge related commits into one bullet. Mention releases or deployments when present.\n" +
        "- Never invent work that is not in the input. If a project's input is thin, write fewer bullets.\n" +
        "- Plain GitHub-flavored markdown. No preamble, no closing remarks, no headings other than the project ones.";

    public static string Build(IReadOnlyList<RepoActivity> repos, WeekWindow window)
    {
        var sb = new StringBuilder();
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
                    var body = Truncate(pr.Body, 600);
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

    public async Task<string> SummarizeAsync(IReadOnlyList<RepoActivity> repos, WeekWindow window, CancellationToken ct)
    {
        var input = SummaryInput.Build(repos, window);
        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 4000,
                System = SummaryInput.SystemPrompt,
                Messages = [new() { Role = Role.User, Content = input }],
            }, cancellationToken: ct);

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
            if (text.Length == 0) throw new InvalidOperationException("Model returned no text.");
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
    public Task<string> SummarizeAsync(IReadOnlyList<RepoActivity> repos, WeekWindow window, CancellationToken ct) =>
        Task.FromResult(SummaryInput.Fallback(repos));
}
