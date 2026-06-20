using System.Net;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Replaces the real outbound HTTP transport so integration tests exercise the full pipeline
/// (endpoint → router → transformer → forwarder) without any network or container. Captures the
/// request the forwarder produced and returns a canned response.
/// </summary>
public sealed class StubUpstreamHandler : HttpMessageHandler
{
    /// <summary>Total upstream requests observed. Lets a test assert "zero upstream calls on this path"
    /// (NFR-03) via a before/after delta, robust to test ordering within the shared (serial) fixture.</summary>
    public int RequestCount { get; private set; }

    public Uri? LastRequestUri { get; private set; }
    public HttpMethod? LastRequestMethod { get; private set; }
    public string? LastRequestBody { get; private set; }
    public string? LastAuthorization { get; private set; }
    public string? LastApiKey { get; private set; }
    public string? LastAnthropicVersion { get; private set; }
    public string? LastAnthropicBeta { get; private set; }

    public Func<HttpResponseMessage> ResponseFactory { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"ok":true}""", System.Text.Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequestUri = request.RequestUri;
        LastRequestMethod = request.Method;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        LastAuthorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? auth) ? string.Join(",", auth) : null;
        LastApiKey = request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? key) ? string.Join(",", key) : null;
        LastAnthropicVersion = request.Headers.TryGetValues("anthropic-version", out IEnumerable<string>? ver) ? string.Join(",", ver) : null;
        LastAnthropicBeta = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? beta) ? string.Join(",", beta) : null;

        return ResponseFactory();
    }
}
