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

        services.AddDataProtection();
        services.AddSingleton<IUpstreamForwarder, UpstreamForwarder>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        // Stored passthrough credentials default to an in-memory settings-backed store. Only wire the
        // encrypted EF Core backend when an operator opts in with a connection string.
        string? connectionString = configuration.GetConnectionString("ImposterDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<ICredentialStore, InMemoryCredentialStore>();
        }
        else
        {
            services.AddDbContext<ImposterDbContext>(options => options.UseNpgsql(connectionString));
            services.AddScoped<ICredentialStore, CredentialStore>();
        }

        return services;
    }
}
