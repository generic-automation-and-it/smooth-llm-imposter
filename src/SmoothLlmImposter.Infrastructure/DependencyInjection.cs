using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Infrastructure.Clients.GithubCopilot;
using SmoothLlmImposter.Infrastructure.Clients.Decisions;
using SmoothLlmImposter.Infrastructure.Persistence;
using SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;
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
        IHttpClientBuilder decisionsClient = services.AddHttpClient(DecisionModelClient.HttpClientName, client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        decisionsClient.AddResilienceHandler("decisions-tools-retry", (builder, context) =>
            {
                DecisionsToolsOptions settings = context.ServiceProvider
                    .GetRequiredService<IOptions<ImposterOptions>>().Value.DecisionsTools;
                int maxAttempts = settings.Enabled ? settings.MaxAttempts : 3;
                int timeoutSeconds = settings.Enabled ? settings.TimeoutSeconds : 60;

                if (maxAttempts > 1)
                {
                    builder.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = maxAttempts - 1,
                        Delay = TimeSpan.Zero,
                        ShouldRetryAfterHeader = false,
                        ShouldHandle = args => new ValueTask<bool>(
                            args.Outcome.Exception is HttpRequestException or TimeoutRejectedException ||
                            args.Outcome.Result?.StatusCode is
                                System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable),
                        OnRetry = args =>
                        {
                            args.Outcome.Result?.Dispose();
                            return default;
                        }
                    });
                }

                builder.AddTimeout(TimeSpan.FromSeconds(timeoutSeconds));
            });
        decisionsClient.AddHttpMessageHandler(static () => new DecisionResponseBufferingHandler());
        services.AddSingleton<IDecisionModelClient, DecisionModelClient>();

        services.AddHttpClient(UpstreamForwarder.HttpClientName, client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("upstream-retry", builder =>
            {
                builder.AddRetry(CreateUpstreamRetryOptions());
                builder.AddTimeout(CreateUpstreamTimeoutOptions());
            });

        // Managed GitHub Copilot traffic uses a sibling client with automatic redirects disabled: the request
        // carries a GitHub Bearer token, and a 30x must not relay it to another origin. The resilience pipeline
        // is identical, so timeout/retry semantics (HLD 012) stay the same.
        services.AddHttpClient(UpstreamForwarder.CopilotHttpClientName, client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddResilienceHandler("upstream-retry-copilot", builder =>
            {
                builder.AddRetry(CreateUpstreamRetryOptions());
                builder.AddTimeout(CreateUpstreamTimeoutOptions());
            });

        services.AddDataProtection();
        services.AddSingleton<IUpstreamForwarder, UpstreamForwarder>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        // Opt-in GitHub Copilot store (R6/R7). Options are bound by the Host and env-mapped/validated by
        // Application: enabling the feature without DatabasePath fails startup, so there is no fallback
        // path that could change between restarts and strand saved connections. While disabled the store
        // has no connection string and is never opened, so no database is created.
        services.AddSingleton<IGithubCopilotConnectionStore>(sp =>
        {
            string? databasePath = sp.GetRequiredService<IOptions<GithubCopilotOptions>>().Value.DatabasePath;
            return new Persistence.GithubCopilot.SqliteGithubCopilotConnectionStore(
                string.IsNullOrWhiteSpace(databasePath)
                    ? null
                    : Persistence.GithubCopilot.SqliteGithubCopilotConnectionStore.BuildConnectionString(databasePath));
        });
        services.AddHostedService<Persistence.GithubCopilot.GithubCopilotBootstrap>();

        // GitHub device flow and account/model discovery. These clients never follow a redirect (a credential
        // must not be relayed to another origin), are time-bounded, and carry no automatic POST retry — the
        // login/refresh state machines own retry decisions. Model discovery is an uncached plural list call.
        services.AddHttpClient(GithubOAuthClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient(GithubCopilotModelsClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IGithubOAuthClient, GithubOAuthClient>();
        services.AddSingleton<IGithubCopilotModelsClient, GithubCopilotModelsClient>();

        // Isolated Copilot protector: its own key ring under the feature data directory, so enabling the
        // feature never reconfigures (or invalidates) the generic credential key ring.
        services.AddSingleton<IGithubCopilotSecretProtector>(sp =>
        {
            string? databasePath = sp.GetRequiredService<IOptions<GithubCopilotOptions>>().Value.DatabasePath;
            string dataDirectory = string.IsNullOrWhiteSpace(databasePath)
                ? Directory.GetCurrentDirectory()
                : Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? Directory.GetCurrentDirectory();

            return DataProtectionGithubCopilotSecretProtector.Create(dataDirectory);
        });

        // Stored passthrough credentials default to an in-memory settings-backed store. Only wire the
        // encrypted EF Core backend when an operator opts in with a connection string. EF Core is not
        // NativeAOT-compatible, so an AOT host with a connection string fail-fasts here with a clear
        // message instead of crashing obscurely inside model building.
        string? connectionString = configuration.GetConnectionString("ImposterDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<ICredentialStore, InMemoryCredentialStore>();
        }
        else
        {
            if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            {
                throw new NotSupportedException(
                    "The ImposterDb EF Core credential store is not supported in NativeAOT builds. " +
                    "Run the JIT container/image when database-backed credential persistence is required.");
            }

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
