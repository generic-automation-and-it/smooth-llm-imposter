using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// The fully-prepared plan for a request: the resolved route, the inbound model (for diagnostics),
/// the transformed JSON body ready to forward upstream, and the session identity to stamp as an
/// outbound header on opted-in imposter routes (SessionIdentity.None for un-opted-in routes).
/// </summary>
public sealed record RoutePlan(
    RouteDecision Decision,
    string InboundModel,
    string TransformedBody,
    SessionIdentity SessionIdentity,
    RouteCredentialOverride? CredentialOverride = null);
