using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

public interface IImposterRouter
{
    Task<RoutePlan> PlanAsync(ApiDialect dialect, string requestBody, CancellationToken cancellationToken);
}
