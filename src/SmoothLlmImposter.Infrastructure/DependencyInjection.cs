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
    ];

    /// <summary>
    /// Base per-attempt header timeout when a provider declares no <c>TimeoutSeconds</c> (HLD 012).
    /// Scaled by <see cref="UpstreamAttemptTimeoutMultipliers"/> into the default 300/600/900 s ladder.
    /// </summary>
    internal const int DefaultUpstreamTimeoutSeconds = 300;

    // Attempt 0 gets the base wait, each retry a proportionally longer one: a first failure is usually a
    // transport hiccup, while a retry is more often a genuinely slow upstream that needs head-room.
    private static readonly int[] UpstreamAttemptTimeoutMultipliers = [1, 2, 3];

    // Set by the forwarder from the resolved route; absent ⇒ DefaultUpstreamTimeoutSeconds.
    internal static readonly HttpRequestOptionsKey<int> UpstreamTimeoutSecondsKey =
        new("smoothllmimposter-upstream-timeout-seconds");

    /// <summary>
    /// Registers the outbound HTTP forwarder and credential persistence. The named client keeps an
    /// infinite timeout for SSE streams and retries pre-response outbound transport failures/timeouts with
    /// fixed delays and progressively longer per-attempt header timeouts. The per-attempt ladder is derived
    /// from the route's per-provider base timeout when the forwarder stamped one on the request.
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
                GetUpstreamAttemptTimeout(
                    GetUpstreamAttemptNumber(args.Context),
                    GetUpstreamTimeoutSeconds(args.Context)) ?? Timeout.InfiniteTimeSpan),
        };

    internal static TimeSpan? GetUpstreamRetryDelay(int attemptNumber) =>
        attemptNumber >= 0 && attemptNumber < UpstreamRetryDelays.Length
            ? UpstreamRetryDelays[attemptNumber]
            : null;

    internal static TimeSpan? GetUpstreamAttemptTimeout(
        int attemptNumber,
        int baseTimeoutSeconds = DefaultUpstreamTimeoutSeconds) =>
        attemptNumber >= 0 && attemptNumber < UpstreamAttemptTimeoutMultipliers.Length
            ? TimeSpan.FromSeconds(baseTimeoutSeconds * UpstreamAttemptTimeoutMultipliers[attemptNumber])
            : null;

    /// <summary>
    /// Reads the per-provider base timeout the forwarder stamped on the outbound request (HLD 012).
    /// Falls back to <see cref="DefaultUpstreamTimeoutSeconds"/> whenever the request is unavailable or
    /// carries no override — including a non-positive value, which the validator rejects at startup but
    /// which must never reach Polly as a zero timeout.
    /// </summary>
    internal static int GetUpstreamTimeoutSeconds(ResilienceContext context) =>
        context.GetRequestMessage() is { } request &&
        request.Options.TryGetValue(UpstreamTimeoutSecondsKey, out int seconds) &&
        seconds > 0
            ? seconds
            : DefaultUpstreamTimeoutSeconds;

    internal static int GetUpstreamAttemptNumber(ResilienceContext context) =>
        context.Properties.TryGetValue(UpstreamAttemptNumberKey, out int attemptNumber)
            ? attemptNumber
            : 0;
}
