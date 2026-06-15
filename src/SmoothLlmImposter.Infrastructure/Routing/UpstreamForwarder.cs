using System.Text;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Infrastructure.Routing;

/// <summary>
/// Forwards the transformed request to the resolved upstream and returns the live response, read
/// headers-first so SSE bodies stream through. Auth is applied per dialect from the provider's
/// configured key; the inbound caller's credentials are never forwarded.
/// </summary>
/// <remarks>
/// The named client uses an infinite <see cref="HttpClient.Timeout"/> and relies on the caller's
/// <see cref="CancellationToken"/>: SSE streams routinely outlive the standard resilience timeouts, and
/// a half-streamed POST cannot be safely retried, so no standard resilience handler is attached.
/// </remarks>
internal sealed class UpstreamForwarder(IHttpClientFactory httpClientFactory, ILogger<UpstreamForwarder> logger)
    : IUpstreamForwarder
{
    internal const string HttpClientName = "imposter-upstream";
    private const string DefaultAnthropicVersion = "2023-06-01";

    public async Task<HttpResponseMessage> SendAsync(
        RouteDecision decision,
        ApiDialect dialect,
        string body,
        string path,
        string? queryString,
        CancellationToken cancellationToken)
    {
        string target = decision.Provider.BaseUrl.AbsoluteUri.TrimEnd('/') + path + (queryString ?? string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        ApplyAuthentication(request, decision, dialect);

        logger.LogDebug("Forwarding to {Provider} at {Target}", decision.Provider.Name, target);

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static void ApplyAuthentication(HttpRequestMessage request, RouteDecision decision, ApiDialect dialect)
    {
        string? apiKey = decision.Provider.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        switch (dialect)
        {
            case ApiDialect.OpenAi:
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                break;

            case ApiDialect.Anthropic:
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation(
                    "anthropic-version",
                    decision.Provider.AnthropicVersion ?? DefaultAnthropicVersion);
                break;
        }
    }
}
