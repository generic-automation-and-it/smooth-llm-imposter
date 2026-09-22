using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure.UnitTest;

/// <summary>
/// Drives the <b>real</b> resilience pipeline registered by <see cref="DependencyInjection.AddInfrastructure"/>
/// with a stalling primary handler. The stamp-on-the-request → <c>TimeoutGenerator</c> seam (HLD 012 LADR-02)
/// is the one hop the other tests exercise only from each side: <c>UpstreamForwarderTimeoutTests</c> asserts the
/// stamp, <c>DependencyInjectionTests</c> calls the generator helpers directly. A handler-order change that made
/// <c>GetRequestMessage()</c> return <c>null</c>, or a retry that rebuilt the request without its
/// <see cref="HttpRequestMessage.Options"/>, would pass both and still ship the option inert.
/// </summary>
/// <remarks>
/// Wall-clock dependent by nature — a timeout only proves itself by elapsing. Kept tolerable by the validator's
/// 1 s floor, and written without fixed sleeps: the stub waits on its own cancellation token, so each attempt
/// ends the instant Polly cancels it rather than after a hard-coded delay.
/// </remarks>
public class UpstreamTimeoutPipelineTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_provider_timeout_bounds_the_attempt_and_the_retry_still_carries_it()
    {
        // TimeoutSeconds: 1 ⇒ ladder 1s/2s/3s. The first attempt stalls past its 1s budget, so Polly must
        // cancel it, surface TimeoutRejectedException to the retry predicate, and re-issue after the 1s delay.
        var handler = new StallingHandler(stallingCalls: 1);
        await using ServiceProvider provider = BuildProvider(handler);
        var forwarder = provider.GetRequiredService<IUpstreamForwarder>();

        var elapsed = Stopwatch.StartNew();
        using HttpResponseMessage response = await forwarder.SendAsync(
            Decision(timeoutSeconds: 1), null, ApiDialect.OpenAi, HttpMethod.Post,
            "{}", "/v1/responses", null, CallerHeaders.None, null, Ct);
        elapsed.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Two calls ⇒ the 1s budget fired. Had the generator fallen back to the 300s default, the stub's own
        // 10s ceiling would have been reached first and this would be a single (very slow) successful call.
        handler.StampedTimeouts.Count.ShouldBe(2);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(25));

        // The retried request must still carry the stamp, or attempts 2 and 3 silently revert to the global
        // default — the operator asked for 601s and would get 601/600/900 instead of 601/1202/1803.
        handler.StampedTimeouts.ShouldAllBe(seconds => seconds == 1);
    }

    [Fact]
    public async Task Without_a_provider_timeout_the_default_base_leaves_a_slow_upstream_alone()
    {
        // Companion to the above: the same stub, unstamped, must NOT be cut off at 1s — proving the bound
        // comes from the provider's value rather than a global constant that happens to match.
        var handler = new StallingHandler(stallingCalls: 1, stall: TimeSpan.FromSeconds(1.5));
        await using ServiceProvider provider = BuildProvider(handler);
        var forwarder = provider.GetRequiredService<IUpstreamForwarder>();

        using HttpResponseMessage response = await forwarder.SendAsync(
            Decision(timeoutSeconds: null), null, ApiDialect.OpenAi, HttpMethod.Post,
            "{}", "/v1/responses", null, CallerHeaders.None, null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.StampedTimeouts.Count.ShouldBe(1);
        handler.StampedTimeouts.Single().ShouldBeNull();
    }

    private static RouteDecision Decision(int? timeoutSeconds) => new(
        new ProviderRoute(
            "upstream",
            ApiDialect.OpenAi,
            new Uri("https://u.example"),
            Secret: "sk-test",
            IsDefault: false,
            AnthropicVersion: null,
            Models: [],
            TimeoutSeconds: timeoutSeconds),
        "target-model",
        CachingEnabled: false,
        IsImposter: true);

    // AddInfrastructure first, then swap only the primary handler: ConfigurePrimaryHttpMessageHandler is
    // additive, so the registered resilience handler stays in the pipeline and genuinely bounds the stub.
    private static ServiceProvider BuildProvider(HttpMessageHandler handler)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .AddHttpClient(UpstreamForwarder.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .Services
            .BuildServiceProvider();
    }

    /// <summary>
    /// Stalls the first <paramref name="stallingCalls"/> attempts, recording the timeout stamped on each
    /// inbound request, then answers immediately. The stall observes its cancellation token so a Polly
    /// timeout ends it at once; <paramref name="stall"/> is only a ceiling that keeps a broken pipeline from
    /// hanging the suite (10s: far above the 1s budget under test, far below the 300s default).
    /// </summary>
    private sealed class StallingHandler(int stallingCalls, TimeSpan? stall = null) : HttpMessageHandler
    {
        private readonly TimeSpan _stall = stall ?? TimeSpan.FromSeconds(10);
        private int _calls;

        public ConcurrentQueue<int?> StampedTimeouts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            StampedTimeouts.Enqueue(
                request.Options.TryGetValue(DependencyInjection.UpstreamTimeoutSecondsKey, out int seconds)
                    ? seconds
                    : null);

            if (Interlocked.Increment(ref _calls) <= stallingCalls)
            {
                await Task.Delay(_stall, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
