using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Façade over the routing pipeline: reads the model from a raw request body, resolves the route, and
/// transforms the body. Pure string-in/string-out so the Host can keep all HTTP concerns to itself.
/// </summary>
public interface IImposterRouter
{
    RoutePlan Plan(ApiDialect dialect, string requestBody);
}
