using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// The fully-prepared plan for a request: the resolved route, the inbound model (for diagnostics),
/// the transformed JSON body ready to forward upstream, the session identity to stamp as an outbound
/// header on opted-in imposter routes (SessionIdentity.None for un-opted-in routes), and — on a matched
/// managed-Copilot route — the exact connection revision the credential came from, so a late upstream 401
/// can invalidate only that revision.
/// </summary>
public sealed record RoutePlan(
    RouteDecision Decision,
    string InboundModel,
    string TransformedBody,
    SessionIdentity SessionIdentity,
    RouteCredentialOverride? CredentialOverride = null,
    GithubCopilotRouteBinding? Copilot = null);

/// <summary>Identifies the managed GitHub Copilot connection revision a route was planned against.</summary>
public sealed record GithubCopilotRouteBinding(string Prefix, long Revision);
