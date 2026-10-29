using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Infrastructure.Clients.Decisions;
using SmoothLlmImposter.Infrastructure.Clients.Optimizer;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Clients;

public class LocalLlmGateTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_optimizer_call_waits_while_a_decision_call_holds_the_slot()
    {
        var decisionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDecision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new CallbackHandler(async (_, ct) =>
        {
            decisionEntered.TrySetResult();
            await releaseDecision.Task.WaitAsync(ct);
            return DecisionSuccess();
        });
        var optimizer = new CallbackHandler((_, _) => Task.FromResult(OptimizerSuccess()));
        await using ServiceProvider provider = BuildProvider(decisions, optimizer);
        using CancellationTokenSource ceiling = Ceiling();

        Task<JsonObject> decisionCall = provider.GetRequiredService<IDecisionModelClient>()
            .DecideAsync(new JsonObject(), ceiling.Token);
        await decisionEntered.Task.WaitAsync(ceiling.Token);
        Task<string> optimizerCall = provider.GetRequiredService<IOptimizerClient>()
            .OptimizeAsync("i", "u", ceiling.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ceiling.Token);

        optimizer.Calls.ShouldBe(0);
        optimizerCall.IsCompleted.ShouldBeFalse();
        releaseDecision.TrySetResult();
        await Task.WhenAll(decisionCall, optimizerCall);
        optimizer.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_decision_call_waits_while_an_optimizer_call_holds_the_slot()
    {
        var optimizerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOptimizer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new CallbackHandler((_, _) => Task.FromResult(DecisionSuccess()));
        var optimizer = new CallbackHandler(async (_, ct) =>
        {
            optimizerEntered.TrySetResult();
            await releaseOptimizer.Task.WaitAsync(ct);
            return OptimizerSuccess();
        });
        await using ServiceProvider provider = BuildProvider(decisions, optimizer);
        using CancellationTokenSource ceiling = Ceiling();

        Task<string> optimizerCall = provider.GetRequiredService<IOptimizerClient>()
            .OptimizeAsync("i", "u", ceiling.Token);
        await optimizerEntered.Task.WaitAsync(ceiling.Token);
        Task<JsonObject> decisionCall = provider.GetRequiredService<IDecisionModelClient>()
            .DecideAsync(new JsonObject(), ceiling.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ceiling.Token);

        decisions.Calls.ShouldBe(0);
        releaseOptimizer.TrySetResult();
        await Task.WhenAll(decisionCall, optimizerCall);
        decisions.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task An_optimizer_call_without_a_slot_within_the_wait_budget_throws_the_gate_timeout()
    {
        var decisionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new CallbackHandler(async (_, ct) =>
        {
            decisionEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return DecisionSuccess();
        });
        var optimizer = new CallbackHandler((_, _) => Task.FromResult(OptimizerSuccess()));
        await using ServiceProvider provider = BuildProvider(decisions, optimizer, gateWaitSeconds: 1);
        using CancellationTokenSource ceiling = Ceiling();
        using var cancelDecision = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);

        Task<JsonObject> decisionCall = provider.GetRequiredService<IDecisionModelClient>()
            .DecideAsync(new JsonObject(), cancelDecision.Token);
        await decisionEntered.Task.WaitAsync(ceiling.Token);

        await Should.ThrowAsync<LocalLlmGateTimeoutException>(() =>
            provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("i", "u", ceiling.Token));

        optimizer.Calls.ShouldBe(0);
        await cancelDecision.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => decisionCall);
    }

    [Fact]
    public async Task The_slot_is_held_across_a_retry_and_its_delay()
    {
        var events = new List<string>();
        var firstAttemptDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new CallbackHandler((call, _) =>
        {
            lock (events)
            {
                events.Add($"decision-{call}");
            }

            if (call == 1)
            {
                firstAttemptDone.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            return Task.FromResult(DecisionSuccess());
        });
        var optimizer = new CallbackHandler((_, _) =>
        {
            lock (events)
            {
                events.Add("optimizer");
            }

            return Task.FromResult(OptimizerSuccess());
        });
        await using ServiceProvider provider = BuildProvider(decisions, optimizer, decisionRetryDelaySeconds: 1);
        using CancellationTokenSource ceiling = Ceiling();

        Task<JsonObject> decisionCall = provider.GetRequiredService<IDecisionModelClient>()
            .DecideAsync(new JsonObject(), ceiling.Token);
        await firstAttemptDone.Task.WaitAsync(ceiling.Token);
        Task<string> optimizerCall = provider.GetRequiredService<IOptimizerClient>()
            .OptimizeAsync("i", "u", ceiling.Token);
        await Task.WhenAll(decisionCall, optimizerCall);

        events.ShouldBe(["decision-1", "decision-2", "optimizer"]);
    }

    [Fact]
    public async Task Decision_and_optimizer_calls_bypass_the_outbound_upstream_gate_which_still_gates_upstreams()
    {
        var decisions = new CallbackHandler((_, _) => Task.FromResult(DecisionSuccess()));
        var optimizer = new CallbackHandler((_, _) => Task.FromResult(OptimizerSuccess()));
        var upstream = new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await using ServiceProvider provider = BuildProvider(decisions, optimizer, upstream);
        SemaphoreSlim outboundGate = provider.GetRequiredService<SemaphoreSlim>();
        await outboundGate.WaitAsync(Ct);
        using CancellationTokenSource ceiling = Ceiling();

        try
        {
            await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(new JsonObject(), ceiling.Token);
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
        HttpMessageHandler optimizer,
        HttpMessageHandler? upstream = null,
        int gateWaitSeconds = LocalLlmGateOptions.DefaultWaitSeconds,
        int decisionRetryDelaySeconds = 0)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<DecisionServiceOptions>(options =>
        {
            options.BaseUrl = "https://decisions.test";
            options.RetryDelaySeconds = decisionRetryDelaySeconds;
        });
        services.Configure<LlmMessagesOptions>(options =>
        {
            options.BaseUrl = "https://opt.test";
            options.Model = "m";
            options.RetryDelaySeconds = 0;
        });
        services.Configure<LocalLlmGateOptions>(options => options.WaitSeconds = gateWaitSeconds);
        services.AddSingleton<IDecisionServiceConsumer>(new OptedInConsumer());
        services.AddInfrastructure(configuration);
        services.AddHttpClient(DecisionModelClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => decisions);
        services.AddHttpClient(OptimizerClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => optimizer);
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
