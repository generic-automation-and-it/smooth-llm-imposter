using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Rewrites an inbound JSON request body for its target: applies the model rename and, when the
/// decision opts in, injects dialect-appropriate prompt caching and (on matched imposter routes)
/// session-identity stamping. Pure string→string; no I/O.
/// </summary>
public interface IRequestTransformer
{
    ApiDialect Dialect { get; }

    /// <param name="requestBody">Raw inbound JSON body.</param>
    /// <param name="decision">The resolved route (carries target model + caching flag).</param>
    /// <param name="inboundModel">The original model name, used as a stable cache key where applicable.</param>
    /// <param name="sessionIdentity">
    /// Resolved session identity for this request. Stamped only on a matched imposter route whose
    /// provider opted into <c>SessionForwarding</c>; otherwise ignored. Never logged as a raw value.
    /// </param>
    /// <returns>The transformed JSON body to forward upstream.</returns>
    string Transform(
        string requestBody,
        RouteDecision decision,
        string inboundModel,
        SessionIdentity sessionIdentity);
}
