using ShippedLog;

namespace ShippedLog.Tests;

public class MarkdownWriterTests
{
    private static readonly WeekWindow W42 = WeekWindow.EndingAt(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void IsoWeekIdUsesLastCoveredInstant()
    {
        Assert.Equal("2026-W42", W42.IsoWeekId);
        Assert.Equal("release-notes/weeks/2026-W42.md", MarkdownWriter.WeekFileName(W42));
        // Window ending Monday Jan 5 2026 00:00 covers Dec 29 to Jan 4, which is ISO 2026-W01.
        Assert.Equal("2026-W01", WeekWindow.EndingAt(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)).IsoWeekId);
    }

    [Fact]
    public void QuietWeekStillRendersFile()
    {
        var md = MarkdownWriter.RenderWeek(W42, [], null);
        Assert.Contains(MarkdownWriter.QuietWeekText, md);
        Assert.DoesNotContain("<details>", md);
    }

    [Fact]
    public void OneLinerExtractsSentence()
    {
        Assert.Equal("Shipped tags and a scoring model.",
            MarkdownWriter.OneLiner("**Week in a sentence:** Shipped tags and a scoring model.\n\n## repo\n- x"));
        Assert.Equal("Plain first line", MarkdownWriter.OneLiner("\nPlain first line\nmore"));
    }

    [Fact]
    public void MarkdownSubsetConvertsToHtml()
    {
        var md = "# Title\n\n_4 days | 2 repos_\n\n**Week in a sentence:** Did a & b.\n\n## org/repo\n- Added [tags](https://x.y/z) with `code`\n- Second\n\n<details><summary>Raw</summary>\n\nplain\n\n</details>\n";
        var html = MarkdownWriter.MarkdownToHtml(md, headingShift: 1);

        Assert.Contains("<h2>Title</h2>", html);
        Assert.Contains("<p><em>4 days | 2 repos</em></p>", html);
        Assert.Contains("<p><strong>Week in a sentence:</strong> Did a &amp; b.</p>", html);
        Assert.Contains("<h3>org/repo</h3>", html);
        Assert.Contains("<li>Added <a href=\"https://x.y/z\">tags</a> with <code>code</code></li>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("</ul>", html);
        Assert.Contains("<details><summary>Raw</summary>", html);
        Assert.Contains("<p>plain</p>", html);
        Assert.Contains("</details>", html);
    }

    [Fact]
    public void InlineHtmlInModelTextIsEscaped()
    {
        var html = MarkdownWriter.MarkdownToHtml("- fixed <script>alert(1)</script> bug");
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void SitePageOrdersNewestFirstWithAnchors()
    {
        var page = MarkdownWriter.RenderSitePage(
        [
            ("2026-W41", "# 2026-W41\n\nNo shipped changes this week.\n"),
            ("2026-W42", "# 2026-W42\n\n**Week in a sentence:** Big week.\n"),
        ]);

        Assert.Contains("<link rel=\"stylesheet\" href=\"../css/style.css\">", page);
        Assert.Contains("<div class=\"content col-6\">", page);
        Assert.Contains("<a href=\"../index.html\">Home</a>", page);
        Assert.True(page.IndexOf("id=\"2026-W42\"", StringComparison.Ordinal) < page.IndexOf("id=\"2026-W41\"", StringComparison.Ordinal));
        Assert.Contains("<a href=\"#2026-W42\">2026-W42</a>", page);
        Assert.Contains("<h2>2026-W42</h2>", page);
    }

    [Fact]
    public void SitePageWithNoWeeksStillRenders()
    {
        var page = MarkdownWriter.RenderSitePage([]);
        Assert.Contains("Nothing logged yet", page);
    }

    [Fact]
    public void SummaryInputGroupsByRepo()
    {
        var repos = NoiseFilterTests.LoadFixture().Select(NoiseFilter.Apply).Where(r => r.HasShippedWork).ToList();
        var input = SummaryInput.Build(repos, W42);
        Assert.Contains("# example/alpha", input);
        Assert.Contains("- #42 Relevance scoring for feed items", input);
        Assert.Contains("Deployments: production x1", input);
        Assert.Contains("- v0.1.0 First playable", input);
        Assert.DoesNotContain("example/quiet", input);
    }
}
