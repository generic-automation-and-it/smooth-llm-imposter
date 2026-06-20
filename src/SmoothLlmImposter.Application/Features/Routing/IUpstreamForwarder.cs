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
    /// <param name="credentialOverride">Optional stored credential, present only on passthrough/default routes.</param>
    /// <param name="dialect">Wire dialect, selecting auth header style.</param>
    /// <param name="method">Inbound HTTP method, forwarded as-is (e.g. <c>POST</c> for completions, <c>GET</c> for model discovery).</param>
    /// <param name="body">Transformed JSON body to send, or <c>null</c> for a body-less request (e.g. a GET probe).</param>
    /// <param name="path">Upstream request path, appended to the provider base URL (e.g. <c>/v1/messages</c>). For
    /// dialect-prefixed inbound routes this is the path with the <c>/openai</c> or <c>/anthropic</c> prefix already stripped.</param>
    /// <param name="queryString">Inbound query string including leading '?', or null.</param>
    /// <param name="callerHeaders">
    /// The caller's full inbound header set. The forwarder relays it verbatim (minus hop-by-hop/content
    /// headers) so the request is proxied unchanged; only the auth header is managed — the caller's own
    /// credential is forwarded on key-less passthrough, or replaced by the provider key / stored credential /
    /// force-Bearer override.
    /// </param>
    Task<HttpResponseMessage> SendAsync(
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        HttpMethod method,
        string? body,
        string path,
        string? queryString,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken);
}
