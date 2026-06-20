using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>Resolves an inbound (dialect, model) pair to a <see cref="RouteDecision"/>.</summary>
public interface IRouteResolver
{
    RouteDecision Resolve(ApiDialect dialect, string model);

    /// <summary>
    /// Resolves the dialect's default provider for a body-less request (e.g. <c>GET /v1/models</c>) that
    /// carries no model to match on. Passthrough only — no rewrite, no caching. Throws
    /// <see cref="RoutingException"/> (404) when the dialect has no default provider.
    /// </summary>
    RouteDecision ResolveDefault(ApiDialect dialect);
}
