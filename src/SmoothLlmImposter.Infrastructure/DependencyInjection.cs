using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Infrastructure.Persistence;
using SmoothLlmImposter.Infrastructure.Persistence.Stores;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure;

public static class DependencyInjection
{
    private static readonly ResiliencePropertyKey<int> UpstreamAttemptNumberKey = new("smoothllmimposter-upstream-attempt");

    private static readonly TimeSpan[] UpstreamRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    private static readonly TimeSpan[] UpstreamAttemptTimeouts =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(300),
        TimeSpan.FromSeconds(600),
    ];

    /// <summary>
    /// Registers the outbound HTTP forwarder and credential persistence. The named client keeps an
    /// infinite timeout for SSE streams and retries pre-response outbound transport failures/timeouts with
    /// fixed delays and progressively longer per-attempt header timeouts.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(UpstreamForwarder.HttpClientName, client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("upstream-retry", builder =>
            {
                builder.AddRetry(CreateUpstreamRetryOptions());
                builder.AddTimeout(CreateUpstreamTimeoutOptions());
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
            ShouldHandle = args => new ValueTask<bool>(
                args.Outcome.Exception is HttpRequestException or TimeoutRejectedException),
            MaxRetryAttempts = UpstreamRetryDelays.Length,
            ShouldRetryAfterHeader = false,
            DelayGenerator = args => new ValueTask<TimeSpan?>(GetUpstreamRetryDelay(args.AttemptNumber)),
            OnRetry = args =>
            {
                args.Context.Properties.Set(UpstreamAttemptNumberKey, args.AttemptNumber + 1);
                return default;
            },
        };

    internal static HttpTimeoutStrategyOptions CreateUpstreamTimeoutOptions() =>
        new()
        {
            TimeoutGenerator = args => new ValueTask<TimeSpan>(
                GetUpstreamAttemptTimeout(GetUpstreamAttemptNumber(args.Context)) ?? Timeout.InfiniteTimeSpan),
        };

    internal static TimeSpan? GetUpstreamRetryDelay(int attemptNumber) =>
        attemptNumber >= 0 && attemptNumber < UpstreamRetryDelays.Length
            ? UpstreamRetryDelays[attemptNumber]
            : null;

    internal static TimeSpan? GetUpstreamAttemptTimeout(int attemptNumber) =>
        attemptNumber >= 0 && attemptNumber < UpstreamAttemptTimeouts.Length
            ? UpstreamAttemptTimeouts[attemptNumber]
            : null;

    internal static int GetUpstreamAttemptNumber(ResilienceContext context) =>
        context.Properties.TryGetValue(UpstreamAttemptNumberKey, out int attemptNumber)
            ? attemptNumber
            : 0;
}
