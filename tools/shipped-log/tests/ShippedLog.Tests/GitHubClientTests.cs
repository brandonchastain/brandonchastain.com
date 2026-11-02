using System.Text.Json.Nodes;
using ShippedLog;

namespace ShippedLog.Tests;

public class GitHubClientTests
{
    private static JsonNode Repo(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void PublicRepoIsNotPrivate() =>
        Assert.False(GitHubClient.IsPrivate(Repo("""{"private":false,"visibility":"public"}""")));

    [Theory]
    [InlineData("""{"private":true,"visibility":"private"}""")]
    [InlineData("""{"private":true}""")]
    [InlineData("""{"private":false,"visibility":"internal"}""")]
    public void PrivateOrInternalRepoIsPrivate(string json) =>
        Assert.True(GitHubClient.IsPrivate(Repo(json)));

    [Fact]
    public void MissingFlagIsTreatedAsPrivate() =>
        Assert.True(GitHubClient.IsPrivate(Repo("""{"full_name":"owner/repo"}""")));

    [Fact]
    public void NullRepoIsTreatedAsPrivate() => Assert.True(GitHubClient.IsPrivate(null));
}
