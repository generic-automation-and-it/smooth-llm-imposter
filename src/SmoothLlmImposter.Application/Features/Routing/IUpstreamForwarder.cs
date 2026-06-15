using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Sends the transformed request to the resolved upstream and returns the live response for the Host
/// to stream back to the caller. Implemented in Infrastructure; the response is read headers-first so
/// SSE bodies flow through without buffering.
/// </summary>
public interface IUpstreamForwarder
{
    /// <param name="decision">Resolved route (provider base URL, key, dialect-specific headers).</param>
    /// <param name="dialect">Wire dialect, selecting auth header style.</param>
    /// <param name="body">Transformed JSON body to send.</param>
    /// <param name="path">Inbound request path, appended to the provider base URL (e.g. <c>/v1/messages</c>).</param>
    /// <param name="queryString">Inbound query string including leading '?', or null.</param>
    Task<HttpResponseMessage> SendAsync(
        RouteDecision decision,
        ApiDialect dialect,
        string body,
        string path,
        string? queryString,
        CancellationToken cancellationToken);
}
