using System.Text;
using System.Text.RegularExpressions;

namespace ShippedLog;

public static partial class MarkdownWriter
{
    public const string QuietWeekText = "No shipped changes this week.";
    public const string WeeksDir = "shipped/weeks";
    public const string PageFile = "shipped/index.html";

    public static string WeekFileName(WeekWindow window) => $"{WeeksDir}/{window.IsoWeekId}.md";

    /// <summary>Full contents of shipped/weeks/YYYY-Www.md.</summary>
    public static string RenderWeek(WeekWindow window, IReadOnlyList<RepoActivity> repos, string? summaryMarkdown)
    {
        var active = repos.Where(r => r.HasShippedWork).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# {window.IsoWeekId} ({window.Start:MMM d} to {window.End.AddDays(-1):MMM d, yyyy})");
        sb.AppendLine();
        if (active.Count == 0)
        {
            sb.AppendLine(QuietWeekText);
            return sb.ToString();
        }

        var stats = WeekStats.From(active);
        sb.AppendLine($"_{stats.ActiveDays} active day{Plural(stats.ActiveDays)} | {stats.ReposTouched} repo{Plural(stats.ReposTouched)} | {stats.PullRequestsMerged} PR{Plural(stats.PullRequestsMerged)} merged | {stats.Commits} commit{Plural(stats.Commits)}_");
        sb.AppendLine();
        sb.AppendLine((summaryMarkdown ?? SummaryInput.Fallback(repos)).Trim());
        sb.AppendLine();
        sb.AppendLine("<details><summary>Raw activity</summary>");
        sb.AppendLine();
        foreach (var repo in active)
        {
            sb.AppendLine($"**[{repo.Name}]({repo.Url})**");
            sb.AppendLine();
            foreach (var pr in repo.MergedPullRequests)
                sb.AppendLine($"- PR [#{pr.Number}]({pr.Url}) {pr.Title}");
            foreach (var c in repo.Commits.OrderBy(c => c.Date))
                sb.AppendLine($"- [`{c.Sha[..Math.Min(7, c.Sha.Length)]}`]({repo.Url}/commit/{c.Sha}) {c.Subject}");
            foreach (var r in repo.Releases)
                sb.AppendLine($"- Release [{r.Tag}]({r.Url}) {r.Name}");
            foreach (var g in repo.Deployments.GroupBy(d => d.Environment))
                sb.AppendLine($"- Deployed to {g.Key} ({g.Count()}x)");
            sb.AppendLine();
        }
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    /// <summary>Extracts the "week in a sentence" line for the notification.</summary>
    public static string OneLiner(string summaryMarkdown)
    {
        var m = OneLinerRegex().Match(summaryMarkdown);
        var line = m.Success
            ? m.Groups[1].Value
            : summaryMarkdown.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        return line.Trim().Trim('*').Trim();
    }

    [GeneratedRegex(@"\*\*Week in a sentence:\*\*\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex OneLinerRegex();

    /// <summary>
    /// The site page shipped/index.html: every week, newest first, rendered from the markdown files.
    /// Follows the site's hand-written page conventions (style.css, content col-6, Home link).
    /// </summary>
    public static string RenderSitePage(IEnumerable<(string WeekId, string Markdown)> weeks)
    {
        var ordered = weeks.OrderByDescending(w => w.WeekId, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("  <head>");
        sb.AppendLine("      <meta charset=\"utf-8\">");
        sb.AppendLine("      <title>Shipped - Brandon Chastain</title>");
        sb.AppendLine("      <link rel=\"stylesheet\" href=\"../css/style.css\">");
        sb.AppendLine("      <meta name=\"viewport\" content=\"width=device-width, initial-scale=1, maximum-scale=1\">");
        sb.AppendLine("  </head>");
        sb.AppendLine("  <body>");
        sb.AppendLine("    <div class=\"content col-6\">");
        sb.AppendLine("      <a href=\"../index.html\">Home</a>");
        sb.AppendLine("      <h3>Shipped</h3>");
        sb.AppendLine("      <p>A weekly log of what I shipped across my side projects, written by a bot every Sunday from the week's commits and pull requests.</p>");
        if (ordered.Count == 0)
        {
            sb.AppendLine("      <p><em>Nothing logged yet.</em></p>");
        }
        else
        {
            sb.AppendLine("      <p>");
            sb.AppendLine("        " + string.Join(" &middot;\n        ", ordered.Select(w => $"<a href=\"#{w.WeekId}\">{w.WeekId}</a>")));
            sb.AppendLine("      </p>");
            foreach (var (weekId, markdown) in ordered)
            {
                sb.AppendLine($"      <section id=\"{weekId}\">");
                sb.Append(MarkdownToHtml(markdown, indent: "        ", headingShift: 1));
                sb.AppendLine("      </section>");
                sb.AppendLine("      <hr/>");
            }
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("  </body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// Converts the small markdown subset this tool emits (and the model is told to emit):
    /// headings, bullets, paragraphs, bold, italics, inline code, links, and raw details blocks.
    /// Not a general markdown parser.
    /// </summary>
    public static string MarkdownToHtml(string markdown, string indent = "", int headingShift = 0)
    {
        var sb = new StringBuilder();
        var inList = false;
        var para = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            sb.AppendLine($"{indent}<p>{Inline(string.Join(" ", para))}</p>");
            para.Clear();
        }
        void CloseList()
        {
            if (!inList) return;
            sb.AppendLine($"{indent}</ul>");
            inList = false;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0) { FlushPara(); CloseList(); continue; }

            if (trimmed.StartsWith('<')) // raw html passthrough: <details>, <summary>, </details>
            {
                FlushPara(); CloseList();
                sb.AppendLine($"{indent}{trimmed}");
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                FlushPara(); CloseList();
                var level = Math.Min(6, heading.Groups[1].Value.Length + headingShift);
                sb.AppendLine($"{indent}<h{level}>{Inline(heading.Groups[2].Value)}</h{level}>");
                continue;
            }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                FlushPara();
                if (!inList) { sb.AppendLine($"{indent}<ul>"); inList = true; }
                sb.AppendLine($"{indent}  <li>{Inline(trimmed[2..])}</li>");
                continue;
            }

            if (inList && line.StartsWith("  ")) // continuation of a bullet
            {
                sb.AppendLine($"{indent}  {Inline(trimmed)}");
                continue;
            }

            CloseList();
            para.Add(trimmed);
        }
        FlushPara(); CloseList();
        return sb.ToString();
    }

    private static string Inline(string text)
    {
        var s = Escape(text);
        s = CodeRegex().Replace(s, "<code>$1</code>");
        s = LinkRegex().Replace(s, "<a href=\"$2\">$1</a>");
        s = BoldRegex().Replace(s, "<strong>$1</strong>");
        s = ItalicRegex().Replace(s, "<em>$1</em>");
        return s;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkRegex();
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();
    [GeneratedRegex(@"(?<![\w*])_([^_\n]+)_(?![\w*])")]
    private static partial Regex ItalicRegex();

    private static string Plural(int n) => n == 1 ? "" : "s";
}
