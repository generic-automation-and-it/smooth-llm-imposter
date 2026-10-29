extern alias HostApp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// HLD 017 LADR-03 / #164 FR-16: across requests, at most one optimizer call is in flight per optimizer endpoint
/// and model. The optimizer stub holds each call until the test releases it, and the clock reports when a request
/// starts its admission deadline, so the test knows the second request is queued without sleeping.
/// </summary>
public sealed class MessageOptimizerAdmissionTests
{
    private const int AdmissionWaitSeconds = 37;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_concurrent_requests_never_have_overlapping_optimizer_calls()
    {
        using var app = new App();

        Task<HttpResponseMessage> first = app.SendAsync("first-session", "Please kindly review `alpha()` now.", Ct);
        OptimizerCall firstCall = await app.Optimizer.NextCallAsync(Bound, Ct);
        Task secondQueued = app.Clock.AdmissionWaitStarted;
        Task<HttpResponseMessage> second = app.SendAsync("second-session", "Please kindly review `beta()` now.", Ct);
        Task<OptimizerCall> secondCall = app.Optimizer.NextCallAsync(Bound, Ct);

        Task arrived = await Task.WhenAny(secondQueued, secondCall).WaitAsync(Bound, Ct);

        arrived.ShouldBe(secondQueued, "the second request must queue for admission, not reach the optimizer");
        app.Optimizer.MaxInFlight.ShouldBe(1);
        firstCall.Release();
        (await secondCall.WaitAsync(Bound, Ct)).Release();
        using HttpResponseMessage firstResponse = await first.WaitAsync(Bound, Ct);
        using HttpResponseMessage secondResponse = await second.WaitAsync(Bound, Ct);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Optimizer.Calls.ShouldBe(2);
        app.Optimizer.MaxInFlight.ShouldBe(1);
    }

    private sealed class OptimizerCall
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => _release.Task;

        public void Release() => _release.TrySetResult();
    }

    /// <summary>Optimizer transport that answers a rewrite only when the test releases the call.</summary>
    private sealed class BlockingOptimizer : HttpMessageHandler
    {
        private readonly Channel<OptimizerCall> _calls = Channel.CreateUnbounded<OptimizerCall>();
        private int _inFlight;
        private int _maxInFlight;
        private int _count;

        public int Calls => Volatile.Read(ref _count);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public Task<OptimizerCall> NextCallAsync(TimeSpan bound, CancellationToken cancellationToken) =>
            _calls.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(bound, cancellationToken);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string userText = JsonNode.Parse(body)!["messages"]![1]!["content"]!.GetValue<string>();
            Interlocked.Increment(ref _count);
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)))
            {
                Interlocked.CompareExchange(ref _maxInFlight, now, seen);
            }

            var call = new OptimizerCall();
            _calls.Writer.TryWrite(call);
            try
            {
                await call.Released.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }

            return Json(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = Rewrite(userText) }
                })
            });
        }
    }

    private static string Rewrite(string delimited) => delimited
        .Replace("<message_to_rewrite>\n", string.Empty, StringComparison.Ordinal)
        .Replace("\n</message_to_rewrite>", string.Empty, StringComparison.Ordinal)
        .Replace("Please kindly review", "Review", StringComparison.Ordinal);

    /// <summary>System time, plus a signal whenever a request starts waiting for optimizer admission.</summary>
    private sealed class AdmissionSignallingClock : TimeProvider
    {
        private readonly Lock _lock = new();
        private TaskCompletionSource _started = NewSignal();

        public Task AdmissionWaitStarted
        {
            get
            {
                lock (_lock)
                {
                    return _started.Task;
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(AdmissionWaitSeconds))
            {
                TaskCompletionSource started;
                lock (_lock)
                {
                    started = _started;
                    _started = NewSignal();
                }

                started.TrySetResult();
            }

            return timer;
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AlwaysOptimizeGate : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Json(new JsonObject
            {
                ["answers"] = new JsonObject
                {
                    ["optimize"] = new JsonObject
                    {
                        ["probabilities"] = new JsonObject { ["optimize"] = 0.9, ["keep"] = 0.1 }
                    }
                }
            }));
    }

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private sealed class App : IDisposable
    {
        private readonly ImposterAppFixture _base = new();
        private readonly WebApplicationFactory<HostApp::Program> _factory;
        private readonly HttpClient _client;

        public App()
        {
            _factory = _base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.Sources.Clear();
                    config.AddInMemoryCollection(Config());
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                    // The outbound gate is sized at registration, before this config reaches the host, so a
                    // config key cannot widen it; two slots let the second request's gate call proceed while
                    // the first request's optimizer call is held.
                    services.RemoveAll<SemaphoreSlim>();
                    services.AddSingleton(new SemaphoreSlim(2, 2));
                    services.AddHttpClient("decision-service")
                        .ConfigurePrimaryHttpMessageHandler(() => new AlwaysOptimizeGate());
                    services.AddHttpClient("llm-messages")
                        .ConfigurePrimaryHttpMessageHandler(() => Optimizer);
                });
            });
            _client = _factory.CreateClient();
        }

        public BlockingOptimizer Optimizer { get; } = new();

        public AdmissionSignallingClock Clock { get; } = new();

        public Task<HttpResponseMessage> SendAsync(string session, string text, CancellationToken cancellationToken)
        {
            string body = new JsonObject
            {
                ["model"] = "anthropic-imposter",
                ["max_tokens"] = 100,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text })
            }.ToJsonString();
            var request = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", "sk-ant-test");
            request.Headers.Add("session_id", session);
            return _client.SendAsync(request, cancellationToken);
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
            _base.Dispose();
        }

        private static Dictionary<string, string?> Config() => new()
        {
            ["Imposter:Providers:openai-official:Dialect"] = "openai",
            ["Imposter:Providers:openai-official:BaseUrl"] = "https://official.test",
            ["Imposter:Providers:openai-official:IsDefault"] = "true",

            ["Imposter:Providers:anthropic-mapped:Dialect"] = "anthropic",
            ["Imposter:Providers:anthropic-mapped:BaseUrl"] = "https://imposter.test",
            ["Imposter:Providers:anthropic-mapped:Models:0:From"] = "anthropic-imposter",
            ["Imposter:Providers:anthropic-mapped:Models:0:To"] = "upstream-model",

            ["Imposter:LlmMessages:Enabled"] = "true",
            ["Imposter:LlmMessages:BaseUrl"] = "https://opt.test",
            ["Imposter:LlmMessages:Model"] = "opt-model",
            ["Imposter:LlmMessages:MaxAttempts"] = "1",
            ["Imposter:LlmMessages:TimeoutSeconds"] = AdmissionWaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),

            ["Imposter:Decisions:BaseUrl"] = "https://gate.test",
            ["Imposter:Decisions:Path"] = "/v1/systemone",
            ["Imposter:Decisions:Model"] = "gate-model",
            ["Imposter:Decisions:MaxAttempts"] = "1",
            ["Imposter:Decisions:TimeoutSeconds"] = "10"
        };
    }
}
