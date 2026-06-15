using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// The fully-prepared plan for a request: the resolved route, the inbound model (for diagnostics),
/// and the transformed JSON body ready to forward upstream.
/// </summary>
public sealed record RoutePlan(
    RouteDecision Decision,
    string InboundModel,
    string TransformedBody,
    RouteCredentialOverride? CredentialOverride = null);
