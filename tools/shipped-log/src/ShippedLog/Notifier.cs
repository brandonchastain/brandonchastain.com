namespace ShippedLog;

/// <summary>
/// Push notification through ntfy: a POST to https://&lt;server&gt;/&lt;topic&gt; with headers.
/// Same private-topic approach as the Azure cost watchdog: the topic name is the secret.
/// </summary>
public sealed class NtfyNotifier
{
    public const string DefaultServer = "https://ntfy.sh";

    private readonly HttpClient _http;
    private readonly string _topicUrl;
    private readonly string? _accessToken;

    public NtfyNotifier(string topic, string? server = null, string? accessToken = null, HttpClient? http = null)
    {
        _topicUrl = $"{(server ?? DefaultServer).TrimEnd('/')}/{topic.Trim('/')}";
        _accessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
        _http = http ?? new HttpClient();
    }

    /// <summary>Builds a notifier from NTFY_TOPIC, NTFY_SERVER and NTFY_ACCESS_TOKEN; null when no topic is set.</summary>
    public static NtfyNotifier? FromEnvironment()
    {
        var topic = Environment.GetEnvironmentVariable("NTFY_TOPIC");
        if (string.IsNullOrWhiteSpace(topic)) return null;
        return new NtfyNotifier(topic,
            Environment.GetEnvironmentVariable("NTFY_SERVER"),
            Environment.GetEnvironmentVariable("NTFY_ACCESS_TOKEN"));
    }

    public async Task SendAsync(string title, string message, string? clickUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _topicUrl)
        {
            Content = new StringContent(message, System.Text.Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Title", title);
        request.Headers.TryAddWithoutValidation("Tags", "package");
        if (clickUrl is not null) request.Headers.TryAddWithoutValidation("Click", clickUrl);
        if (_accessToken is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
