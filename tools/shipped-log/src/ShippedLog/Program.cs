using System.Text.Json;
using ShippedLog;

// Usage:
//   shipped-log [--dry-run] [--no-ai] [--end 2026-10-19] [--fixture path.json] [--out <site root>]
//
// Writes <site root>/release-notes/weeks/YYYY-Www.md and regenerates <site root>/release-notes/index.html.
//
// Env: GITHUB_TOKEN (required unless --fixture), ANTHROPIC_API_KEY (optional; fallback list without it),
//      NTFY_TOPIC (private topic name; optional), NTFY_SERVER (default https://ntfy.sh), NTFY_ACCESS_TOKEN (optional),
//      SITE_URL (default https://brandonchastain.com; used for the notification link).

var options = CliArgs.Parse(args);
var log = Console.Error;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

if (options.RebuildPage)
{
    // Used by the site deploy: regenerate release-notes/index.html from the committed week files.
    // No GitHub, no model, no notification.
    var siteRoot = options.Out ?? Directory.GetCurrentDirectory();
    var count = RebuildPage(siteRoot);
    log.WriteLine($"Rebuilt {MarkdownWriter.PageFile} from {count} week file(s).");
    return 0;
}

var window = WeekWindow.EndingAt(options.End ?? DateTimeOffset.UtcNow);
log.WriteLine($"Window: {window.Start:u} -> {window.End:u} ({window.IsoWeekId})");

// 1. Collect
List<RepoActivity> repos;
if (options.Fixture is not null)
{
    var json = await File.ReadAllTextAsync(options.Fixture, ct);
    repos = JsonSerializer.Deserialize<List<RepoActivity>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Fixture did not parse.");
    log.WriteLine($"Loaded {repos.Count} repos from fixture.");
}
else
{
    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? throw new InvalidOperationException("GITHUB_TOKEN is not set.");
    var gh = new GitHubClient(token);
    var login = await gh.GetLoginAsync(ct);
    var active = await gh.GetActiveReposAsync(window, ct);
    log.WriteLine($"{login}: {active.Count} repo(s) pushed this week.");
    repos = [];
    var skipped = 0;
    foreach (var (name, url, branch) in active)
    {
        try
        {
            var activity = await gh.GetActivityAsync(name, url, branch, login, window, ct);
            log.WriteLine($"  {name}: {activity.Commits.Count} commits, {activity.MergedPullRequests.Count} merged PRs, {activity.Releases.Count} releases");
            repos.Add(activity);
        }
        catch (HttpRequestException ex)
        {
            // Usually a token that can list the repo but not read it. Keep going so one repo never sinks the week.
            skipped++;
            log.WriteLine($"  {name}: skipped ({(int?)ex.StatusCode} {ex.StatusCode}). Check the token's repository access and Contents/Pull requests permissions.");
        }
    }
    if (skipped > 0 && repos.Count == 0)
        throw new InvalidOperationException($"Every active repo was skipped ({skipped}); the GitHub token cannot read them.");
}

// 2. Filter noise, drop repos with nothing left
repos = repos.Select(NoiseFilter.Apply).Where(r => r.HasShippedWork).OrderBy(r => r.Name).ToList();
log.WriteLine($"{repos.Count} repo(s) with shipped work after noise filter.");

// 3. Summarize
string? summary = null;
if (repos.Count > 0)
{
    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    ISummarizer summarizer;
    if (options.NoAi || string.IsNullOrEmpty(apiKey))
    {
        log.WriteLine("Using fallback summarizer (no ANTHROPIC_API_KEY or --no-ai).");
        summarizer = new FallbackSummarizer();
    }
    else
    {
        summarizer = new ClaudeSummarizer(apiKey, log);
    }
    // Earlier recaps go in as context so the model does not re-announce work it already covered.
    var priorWeeks = MarkdownWriter.ReadPriorWeeks(options.Out ?? Directory.GetCurrentDirectory(), window.IsoWeekId);
    if (priorWeeks.Count > 0)
        log.WriteLine($"Prior weeks for context: {string.Join(", ", priorWeeks.Select(w => w.WeekId))}");
    summary = await summarizer.SummarizeAsync(repos, window, priorWeeks, ct);
}

// 4. Render
var weekMarkdown = MarkdownWriter.RenderWeek(window, repos, summary);
var oneLiner = repos.Count == 0 ? MarkdownWriter.QuietWeekText : MarkdownWriter.OneLiner(summary!);
var siteUrl = (Environment.GetEnvironmentVariable("SITE_URL") ?? "https://brandonchastain.com").TrimEnd('/');
var pageLink = $"{siteUrl}/release-notes/#{window.IsoWeekId}";

if (options.DryRun)
{
    Console.WriteLine($"--- {MarkdownWriter.WeekFileName(window)} ---");
    Console.Write(weekMarkdown);
    Console.WriteLine("--- notification ---");
    Console.WriteLine(repos.Count == 0 ? "(skipped: quiet week)" : $"{oneLiner}\n{pageLink}");
    return 0;
}

// 5. Write the week file and regenerate the site page (the workflow commits them)
var root = options.Out ?? Directory.GetCurrentDirectory();
var weeksDir = Path.Combine(root, MarkdownWriter.WeeksDir);
Directory.CreateDirectory(weeksDir);
var weekPath = Path.Combine(root, MarkdownWriter.WeekFileName(window));
await File.WriteAllTextAsync(weekPath, weekMarkdown, ct);
log.WriteLine($"Wrote {weekPath}");

var weekCount = RebuildPage(root);
log.WriteLine($"Wrote {MarkdownWriter.PageFile} ({weekCount} week(s))");

// 6. Notify (skip quiet weeks)
var notifier = NtfyNotifier.FromEnvironment();
if (repos.Count == 0)
{
    log.WriteLine("Quiet week: notification skipped.");
}
else if (notifier is null)
{
    log.WriteLine("NTFY_TOPIC not set: notification skipped.");
}
else
{
    var repoNames = string.Join(", ", repos.Select(r => r.Name.Split('/').Last()));
    var body = $"{oneLiner}\n\n{repos.Count} repo(s): {repoNames}";
    try
    {
        await notifier.SendAsync($"Release notes {window.IsoWeekId}", body, pageLink, ct);
        log.WriteLine("Notification sent.");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        log.WriteLine($"warn: notification failed: {ex.Message}");
    }
}
return 0;

/// <summary>Regenerates release-notes/index.html from every week file; returns how many weeks it found.</summary>
static int RebuildPage(string siteRoot)
{
    var weeksDir = Path.Combine(siteRoot, MarkdownWriter.WeeksDir);
    var weeks = Directory.Exists(weeksDir)
        ? Directory.EnumerateFiles(weeksDir, "*.md")
            .Select(p => (WeekId: Path.GetFileNameWithoutExtension(p), Markdown: File.ReadAllText(p)))
            .ToList()
        : [];
    var pagePath = Path.Combine(siteRoot, MarkdownWriter.PageFile);
    Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
    File.WriteAllText(pagePath, MarkdownWriter.RenderSitePage(weeks));
    return weeks.Count;
}

sealed record CliArgs(bool DryRun, bool NoAi, bool RebuildPage, DateTimeOffset? End, string? Fixture, string? Out)
{
    public static CliArgs Parse(string[] argv)
    {
        bool dry = false, noAi = false, rebuild = false;
        DateTimeOffset? end = null;
        string? fixture = null, outDir = null;
        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--dry-run": dry = true; break;
                case "--no-ai": noAi = true; break;
                case "--rebuild-page": rebuild = true; break;
                case "--end": end = DateTimeOffset.Parse(argv[++i], null, System.Globalization.DateTimeStyles.AssumeUniversal); break;
                case "--fixture": fixture = argv[++i]; break;
                case "--out": outDir = argv[++i]; break;
                default: throw new ArgumentException($"Unknown argument: {argv[i]}");
            }
        }
        return new CliArgs(dry, noAi, rebuild, end, fixture, outDir);
    }
}
