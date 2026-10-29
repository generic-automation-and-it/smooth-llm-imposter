using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;
using SmoothLlmImposter.Application;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Infrastructure.Clients.Decisions;
using SmoothLlmImposter.Infrastructure.Clients.Optimizer;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Clients;

public class LocalLlmSlotTests
{
    private const string Section = "Imposter:DecisionsFixture";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Application_and_infrastructure_register_one_shared_slot()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection([]).Build());

        services.Count(descriptor => descriptor.ServiceType == typeof(LocalLlmSlot)).ShouldBe(1);
    }

    [Fact]
    public async Task A_decision_call_waits_while_another_local_llm_call_holds_the_slot()
    {
        var decisions = new CallbackHandler((_, _) => Task.FromResult(DecisionSuccess()));
        await using ServiceProvider provider = BuildProvider(decisions);
        LocalLlmSlot slot = provider.GetRequiredService<LocalLlmSlot>();
        slot.TryEnter().ShouldBeTrue("an optimizer call holds the shared slot");
        using CancellationTokenSource ceiling = Ceiling();

        Task<JsonObject> decisionCall = provider.GetRequiredService<IDecisionModelClient>()
            .DecideAsync(Section, new JsonObject(), ceiling.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ceiling.Token);

        decisions.Calls.ShouldBe(0);
        decisionCall.IsCompleted.ShouldBeFalse();
        slot.Release();
        await decisionCall;
        decisions.Calls.ShouldBe(1);
        slot.TryEnter().ShouldBeTrue("the decision call released the shared slot");
        slot.Release();
    }

    [Fact]
    public async Task A_decision_call_keeps_the_slot_through_its_retry_delay()
    {
        var firstAttemptFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new CallbackHandler((call, _) =>
        {
            if (call == 1)
            {
                firstAttemptFailed.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            return Task.FromResult(DecisionSuccess());
        });
        await using ServiceProvider provider = BuildProvider(decisions, retryDelaySeconds: 1);
        LocalLlmSlot slot = provider.GetRequiredService<LocalLlmSlot>();
        using CancellationTokenSource ceiling = Ceiling();

        Task<JsonObject> decisionCall = provider.GetRequiredService<IDecisionModelClient>()
            .DecideAsync(Section, new JsonObject(), ceiling.Token);
        await firstAttemptFailed.Task.WaitAsync(ceiling.Token);

        slot.TryEnter().ShouldBeFalse("no other local-LLM call may start during the retry delay");
        await decisionCall;
        decisions.Calls.ShouldBe(2);
        slot.TryEnter().ShouldBeTrue();
        slot.Release();
    }

    [Theory]
    [InlineData(0, 0, 500)]
    [InlineData(1, 900, 5000)]
    public async Task Decision_retries_wait_the_configured_delay(int retryDelaySeconds, int minGapMs, int maxGapMs)
    {
        var attemptTimes = new List<TimeSpan>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var decisions = new CallbackHandler((call, _) =>
        {
            attemptTimes.Add(clock.Elapsed);
            return Task.FromResult(call < 2 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : DecisionSuccess());
        });
        await using ServiceProvider provider = BuildProvider(decisions, retryDelaySeconds);

        await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, new JsonObject(), Ct);

        attemptTimes.Count.ShouldBe(2);
        TimeSpan gap = attemptTimes[1] - attemptTimes[0];
        gap.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(minGapMs));
        gap.ShouldBeLessThan(TimeSpan.FromMilliseconds(maxGapMs));
    }

    [Fact]
    public async Task Decision_and_optimizer_calls_bypass_the_outbound_upstream_gate_which_still_gates_upstreams()
    {
        var decisions = new CallbackHandler((_, _) => Task.FromResult(DecisionSuccess()));
        var optimizer = new CallbackHandler((_, _) => Task.FromResult(OptimizerSuccess()));
        var upstream = new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await using ServiceProvider provider = BuildProvider(decisions, optimizer: optimizer, upstream: upstream);
        SemaphoreSlim outboundGate = provider.GetRequiredService<SemaphoreSlim>();
        await outboundGate.WaitAsync(Ct);
        using CancellationTokenSource ceiling = Ceiling();

        try
        {
            await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, new JsonObject(), ceiling.Token);
            await provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("i", "u", ceiling.Token);

            using HttpClient upstreamClient = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(UpstreamForwarder.HttpClientName);
            using var upstreamWait = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
            upstreamWait.CancelAfter(TimeSpan.FromMilliseconds(300));
            await Should.ThrowAsync<OperationCanceledException>(() =>
                upstreamClient.GetAsync("https://upstream.test/", upstreamWait.Token));
        }
        finally
        {
            outboundGate.Release();
        }

        decisions.Calls.ShouldBe(1);
        optimizer.Calls.ShouldBe(1);
        upstream.Calls.ShouldBe(0);
    }

    private CancellationTokenSource Ceiling()
    {
        var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));
        return ceiling;
    }

    private static ServiceProvider BuildProvider(
        HttpMessageHandler decisions,
        int retryDelaySeconds = 0,
        HttpMessageHandler? optimizer = null,
        HttpMessageHandler? upstream = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<DecisionServiceOptions>(Section, options =>
        {
            options.BaseUrl = "https://decisions.test";
            options.RetryDelaySeconds = retryDelaySeconds;
        });
        services.Configure<LlmMessagesOptions>(options =>
        {
            options.BaseUrl = "https://opt.test";
            options.Model = "m";
        });
        services.AddSingleton<IDecisionServiceConsumer>(new OptedInConsumer());
        services.AddInfrastructure(configuration);
        services.AddHttpClient(DecisionModelClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => decisions);
        if (optimizer is not null)
        {
            services.AddHttpClient(OptimizerClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => optimizer);
        }

        if (upstream is not null)
        {
            services.AddHttpClient(UpstreamForwarder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
        }

        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage DecisionSuccess() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"answers":{}}""", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage OptimizerSuccess() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"choices":[{"message":{"content":"rewritten"}}]}""", Encoding.UTF8, "application/json")
    };

    private sealed class OptedInConsumer : IDecisionServiceConsumer
    {
        public string Section => LocalLlmSlotTests.Section;

        public bool IsOptedIn => true;
    }

    private sealed class CallbackHandler(
        Func<int, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(Interlocked.Increment(ref _calls), cancellationToken);
    }
}
