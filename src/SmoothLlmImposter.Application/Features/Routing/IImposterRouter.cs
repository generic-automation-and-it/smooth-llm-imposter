using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

public interface IImposterRouter
{
    /// <summary>Plans a request that carries a JSON body with a <c>model</c> — imposter match or default passthrough, with body transform.</summary>
    /// <param name="callerHeaders">
    /// Raw inbound header snapshot (used by the resolver to consult the allowlisted
    /// capture headers). The resolver only reads from <see cref="SensitiveHeaderNames"/>-guarded
    /// names; passthrough never reads this — see <see cref="PlanPassthroughAsync"/>.
    /// </param>
    Task<RoutePlan> PlanAsync(
        ApiDialect dialect,
        string requestBody,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken);

    /// <summary>
    /// Plans a body-less request (e.g. <c>GET /v1/models</c>) that has no <c>model</c> to resolve: passthrough to the
    /// dialect's default provider, no transform. Reuses the passthrough credential seam (stored credential / HLD-003 override).
    /// </summary>
    /// <param name="callerHeaders">
    /// Raw inbound header snapshot. Accepted for interface uniformity with <see cref="PlanAsync"/>;
    /// session forwarding never stamps passthrough, so this is unused here.
    /// </param>
    Task<RoutePlan> PlanPassthroughAsync(
        ApiDialect dialect,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken);
}
