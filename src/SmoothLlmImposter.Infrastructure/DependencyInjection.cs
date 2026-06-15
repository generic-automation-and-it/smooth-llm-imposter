using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Infrastructure.Persistence;
using SmoothLlmImposter.Infrastructure.Persistence.Stores;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the outbound HTTP forwarder and credential persistence. The named client uses an
    /// infinite timeout so SSE streams are bounded only by the caller's cancellation token.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(UpstreamForwarder.HttpClientName, client =>
            client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddDbContext<ImposterDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("ImposterDb") ??
                "Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres"));

        services.AddDataProtection();
        services.AddSingleton<IUpstreamForwarder, UpstreamForwarder>();
        services.AddScoped<ICredentialStore, CredentialStore>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        return services;
    }
}
