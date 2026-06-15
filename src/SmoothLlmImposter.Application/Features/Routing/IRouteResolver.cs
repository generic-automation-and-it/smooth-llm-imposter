using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>Resolves an inbound (dialect, model) pair to a <see cref="RouteDecision"/>.</summary>
public interface IRouteResolver
{
    RouteDecision Resolve(ApiDialect dialect, string model);
}
