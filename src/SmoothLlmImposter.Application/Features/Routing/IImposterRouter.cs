using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

public interface IImposterRouter
{
    /// <summary>Plans a request that carries a JSON body with a <c>model</c> — imposter match or default passthrough, with body transform.</summary>
    Task<RoutePlan> PlanAsync(
        ApiDialect dialect,
        string requestBody,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken);

    /// <summary>
    /// Plans a body-less request (e.g. <c>GET /v1/models</c>) that has no <c>model</c> to resolve: passthrough to the
    /// dialect's default provider, no transform. Reuses the passthrough credential seam (stored credential / HLD-003 override).
    /// </summary>
    Task<RoutePlan> PlanPassthroughAsync(
        ApiDialect dialect,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken);
}
