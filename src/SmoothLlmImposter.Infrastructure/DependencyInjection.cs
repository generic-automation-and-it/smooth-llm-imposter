using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Infrastructure.Persistence;
using SmoothLlmImposter.Infrastructure.Persistence.Stores;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure;

public static class DependencyInjection
{
    private static readonly TimeSpan[] UpstreamRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    /// <summary>
    /// Registers the outbound HTTP forwarder and credential persistence. The named client keeps an
    /// infinite timeout for SSE streams and retries transient outbound failures with fixed delays.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(UpstreamForwarder.HttpClientName, client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("upstream-retry", builder =>
            {
                builder.AddRetry(CreateUpstreamRetryOptions());
            });

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

    internal static HttpRetryStrategyOptions CreateUpstreamRetryOptions() =>
        new()
        {
            MaxRetryAttempts = UpstreamRetryDelays.Length,
            ShouldRetryAfterHeader = false,
            DelayGenerator = args => new ValueTask<TimeSpan?>(GetUpstreamRetryDelay(args.AttemptNumber)),
        };

    internal static TimeSpan? GetUpstreamRetryDelay(int attemptNumber) =>
        attemptNumber >= 0 && attemptNumber < UpstreamRetryDelays.Length
            ? UpstreamRetryDelays[attemptNumber]
            : null;
}
