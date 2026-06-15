using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the outbound HTTP forwarder. The named client uses an infinite timeout so SSE streams
    /// are bounded only by the caller's cancellation token (see <see cref="UpstreamForwarder"/>).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddHttpClient(UpstreamForwarder.HttpClientName, client =>
            client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<IUpstreamForwarder, UpstreamForwarder>();

        return services;
    }
}
