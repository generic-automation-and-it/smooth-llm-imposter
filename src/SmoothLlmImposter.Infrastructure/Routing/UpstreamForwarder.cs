using System.Text;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Infrastructure.Routing;

/// <summary>
/// Forwards the transformed request to the resolved upstream and returns the live response, read
/// headers-first so SSE bodies stream through. Auth is applied per dialect from the provider's
/// configured key unless a passthrough credential override is supplied.
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
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        string body,
        string path,
        string? queryString,
        CancellationToken cancellationToken)
    {
        Uri baseUrl = credentialOverride?.BaseUrlOverride ?? decision.Provider.BaseUrl;
        string target = baseUrl.AbsoluteUri.TrimEnd('/') + path + (queryString ?? string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        ApplyAuthentication(request, decision, credentialOverride, dialect);

        logger.LogDebug("Forwarding to {Provider} at {Target}", decision.Provider.Name, target);

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static void ApplyAuthentication(
        HttpRequestMessage request,
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect)
    {
        string? secret = credentialOverride?.Secret ?? decision.Provider.ApiKey;
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        if (credentialOverride is null)
        {
            ApplyDefaultAuthentication(request, dialect, secret);
        }
        else
        {
            ApplyStoredCredentialAuthentication(request, credentialOverride.AuthScheme, secret);
        }

        if (dialect == ApiDialect.Anthropic)
        {
            request.Headers.TryAddWithoutValidation(
                "anthropic-version",
                credentialOverride?.AnthropicVersion ?? decision.Provider.AnthropicVersion ?? DefaultAnthropicVersion);
        }
    }

    private static void ApplyDefaultAuthentication(HttpRequestMessage request, ApiDialect dialect, string secret)
    {
        switch (dialect)
        {
            case ApiDialect.OpenAi:
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
                break;
            case ApiDialect.Anthropic:
                request.Headers.TryAddWithoutValidation("x-api-key", secret);
                break;
        }
    }

    private static void ApplyStoredCredentialAuthentication(
        HttpRequestMessage request,
        CredentialAuthScheme authScheme,
        string secret)
    {
        switch (authScheme)
        {
            case CredentialAuthScheme.Bearer:
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
                break;
            case CredentialAuthScheme.ApiKey:
                request.Headers.TryAddWithoutValidation("x-api-key", secret);
                break;
        }
    }
}
