using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace ShippedLog;

/// <summary>Thin REST client for the parts of the GitHub API this tool needs.</summary>
public sealed class GitHubClient
{
    private readonly HttpClient _http;

    public GitHubClient(string token, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("shipped-log/1.0");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<string> GetLoginAsync(CancellationToken ct)
    {
        var user = await GetJsonAsync("user", ct);
        return user!["login"]!.GetValue<string>();
    }

    /// <summary>Repos owned by the authenticated user with a push inside the window.</summary>
    public async Task<List<(string Name, string Url, string DefaultBranch)>> GetActiveReposAsync(WeekWindow window, CancellationToken ct)
    {
        var result = new List<(string, string, string)>();
        for (var page = 1; page <= 10; page++)
        {
            var repos = await GetJsonAsync(
                $"user/repos?affiliation=owner&sort=pushed&direction=desc&per_page=100&page={page}", ct) as JsonArray;
            if (repos is null || repos.Count == 0) break;

            var olderThanWindow = false;
            foreach (var repo in repos)
            {
                var pushed = repo!["pushed_at"]?.GetValue<DateTimeOffset>();
                if (pushed is null || pushed < window.Start) { olderThanWindow = true; continue; }
                if (repo["fork"]?.GetValue<bool>() == true) continue;
                result.Add((
                    repo["full_name"]!.GetValue<string>(),
                    repo["html_url"]!.GetValue<string>(),
                    repo["default_branch"]?.GetValue<string>() ?? "main"));
            }
            // Sorted by pushed desc: once we see one older than the window we're done.
            if (olderThanWindow || repos.Count < 100) break;
        }
        return result;
    }

    public async Task<RepoActivity> GetActivityAsync(string fullName, string url, string defaultBranch, string login, WeekWindow window, CancellationToken ct)
    {
        var commits = await GetCommitsAsync(fullName, defaultBranch, login, window, ct);
        var prs = await GetMergedPullRequestsAsync(fullName, window, ct);
        var releases = await GetReleasesAsync(fullName, window, ct);
        var deployments = await GetDeploymentsAsync(fullName, window, ct);
        return new RepoActivity(fullName, url, defaultBranch, commits, prs, releases, deployments);
    }

    private async Task<List<Commit>> GetCommitsAsync(string repo, string branch, string login, WeekWindow window, CancellationToken ct)
    {
        var list = new List<Commit>();
        var since = Uri.EscapeDataString(window.Start.UtcDateTime.ToString("o"));
        var until = Uri.EscapeDataString(window.End.UtcDateTime.ToString("o"));
        for (var page = 1; page <= 5; page++)
        {
            var arr = await GetJsonAsync(
                $"repos/{repo}/commits?sha={Uri.EscapeDataString(branch)}&author={Uri.EscapeDataString(login)}&since={since}&until={until}&per_page=100&page={page}", ct) as JsonArray;
            if (arr is null || arr.Count == 0) break;
            foreach (var c in arr)
            {
                var message = c!["commit"]!["message"]!.GetValue<string>();
                var (subject, body) = SplitMessage(message);
                list.Add(new Commit(
                    c["sha"]!.GetValue<string>(),
                    subject,
                    body,
                    c["author"]?["login"]?.GetValue<string>() ?? c["commit"]!["author"]?["name"]?.GetValue<string>() ?? "",
                    c["commit"]!["author"]!["date"]!.GetValue<DateTimeOffset>(),
                    (c["parents"] as JsonArray)?.Count ?? 1));
            }
            if (arr.Count < 100) break;
        }
        return list;
    }

    private async Task<List<PullRequest>> GetMergedPullRequestsAsync(string repo, WeekWindow window, CancellationToken ct)
    {
        var list = new List<PullRequest>();
        var arr = await GetJsonAsync(
            $"repos/{repo}/pulls?state=closed&sort=updated&direction=desc&per_page=50", ct) as JsonArray;
        if (arr is null) return list;
        foreach (var pr in arr)
        {
            var mergedNode = pr!["merged_at"];
            if (mergedNode is null) continue;
            var mergedAt = mergedNode.GetValue<DateTimeOffset>();
            if (mergedAt < window.Start || mergedAt >= window.End) continue;
            if (NoiseFilter.IsBot(pr["user"]?["login"]?.GetValue<string>() ?? "")) continue;
            list.Add(new PullRequest(
                pr["number"]!.GetValue<int>(),
                pr["title"]!.GetValue<string>(),
                pr["body"]?.GetValue<string>() ?? "",
                mergedAt,
                pr["html_url"]!.GetValue<string>()));
        }
        return list;
    }

    private async Task<List<Release>> GetReleasesAsync(string repo, WeekWindow window, CancellationToken ct)
    {
        var list = new List<Release>();
        var arr = await GetJsonAsync($"repos/{repo}/releases?per_page=20", ct) as JsonArray;
        if (arr is null) return list;
        foreach (var r in arr)
        {
            var publishedNode = r!["published_at"];
            if (publishedNode is null) continue;
            var published = publishedNode.GetValue<DateTimeOffset>();
            if (published < window.Start || published >= window.End) continue;
            if (r["draft"]?.GetValue<bool>() == true) continue;
            list.Add(new Release(
                r["tag_name"]!.GetValue<string>(),
                r["name"]?.GetValue<string>() ?? "",
                r["body"]?.GetValue<string>() ?? "",
                published,
                r["html_url"]!.GetValue<string>()));
        }
        return list;
    }

    private async Task<List<Deployment>> GetDeploymentsAsync(string repo, WeekWindow window, CancellationToken ct)
    {
        var list = new List<Deployment>();
        JsonNode? arr;
        try { arr = await GetJsonAsync($"repos/{repo}/deployments?per_page=20", ct); }
        catch (HttpRequestException) { return list; } // deployments are optional
        if (arr is not JsonArray deployments) return list;
        foreach (var d in deployments)
        {
            var created = d!["created_at"]!.GetValue<DateTimeOffset>();
            if (created < window.Start || created >= window.End) continue;
            list.Add(new Deployment(d["environment"]?.GetValue<string>() ?? "unknown", "created", created));
        }
        return list;
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return new JsonArray(); // empty repo
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }

    internal static (string Subject, string Body) SplitMessage(string message)
    {
        var normalized = message.Replace("\r\n", "\n");
        var idx = normalized.IndexOf('\n');
        return idx < 0
            ? (normalized.Trim(), "")
            : (normalized[..idx].Trim(), normalized[(idx + 1)..].Trim());
    }
}
