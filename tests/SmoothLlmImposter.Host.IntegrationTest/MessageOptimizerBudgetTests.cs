extern alias HostApp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// HLD 017 Open item 7: the message strategy spends at most <c>LLM_MESSAGES_BUDGET_SECONDS</c> per request. When the
/// budget runs out mid-call the call is cancelled, finished rewrites still apply, every remaining candidate is
/// forwarded and cached as written, and the next request replays them byte for byte (NFR-01). The optimizer stub
/// stalls until cancelled and the clock fires the budget deadline on demand, so nothing sleeps.
/// </summary>
public sealed class MessageOptimizerBudgetTests
{
    private const int BudgetSeconds = 23;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_spent_budget_forwards_the_rest_as_written_and_the_next_request_replays_it_byte_identically()
    {
        using var app = new App();
        JsonArray history = History("alpha", "beta", "gamma");

        Task<HttpResponseMessage> first = app.SendAsync(history, Ct);
        await app.Optimizer.Stalled.Task.WaitAsync(Bound, Ct);
        app.Clock.ExpireBudgets();
        using (HttpResponseMessage response = await first.WaitAsync(Bound, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        JsonArray forwarded = app.ForwardedMessages();
        Text(forwarded[0]).ShouldBe("Review `alpha()` now.");
        Text(forwarded[2]).ShouldBe(Human("beta"));
        Text(forwarded[4]).ShouldBe(Human("gamma"));
        await app.Optimizer.Cancelled.Task.WaitAsync(Bound, Ct);
        app.Gate.Calls.ShouldBe(2, "the message after the expiry must not reach the gate");
        app.Optimizer.Calls.ShouldBe(2);
        app.Logs.Information().Where(line => line.Contains("budget", StringComparison.Ordinal)).ShouldBe([
            $"Message optimization budget of {BudgetSeconds} s exhausted: 2 of 3 candidate segments forwarded as the original"]);
        app.Logs.ApplicationLines().ShouldAllBe(line => line.Level < LogLevel.Warning, "budget expiry is not a failure");
        app.Logs.ApplicationLines().ShouldAllBe(line => !line.Text.Contains("kindly", StringComparison.Ordinal));

        history.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ok" });
        history.Add(new JsonObject { ["role"] = "user", ["content"] = Human("delta") });
        using (HttpResponseMessage response = await app.SendAsync(history, Ct).WaitAsync(Bound, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        JsonArray replayed = app.ForwardedMessages();
        for (int index = 0; index < forwarded.Count; index++)
        {
            replayed[index]!.ToJsonString().ShouldBe(forwarded[index]!.ToJsonString(), $"message {index} must replay byte for byte");
        }

        Text(replayed[6]).ShouldBe("Review `delta()` now.");
        app.Gate.Calls.ShouldBe(3, "an open gate circuit would skip the gate");
        app.Optimizer.Calls.ShouldBe(3, "an open optimizer circuit would skip the optimizer");
        app.Logs.Information().Count(line => line.Contains("budget", StringComparison.Ordinal)).ShouldBe(1);
    }

    private static string Human(string name) => $"Please kindly review `{name}()` now.";

    private static string Text(JsonNode? message) => message!["content"]!.GetValue<string>();

    private static JsonArray History(params string[] names)
    {
        var history = new JsonArray();
        foreach (string name in names)
        {
            if (history.Count > 0)
            {
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ok" });
            }

            history.Add(new JsonObject { ["role"] = "user", ["content"] = Human(name) });
        }

        return history;
    }

    /// <summary>System time; the budget deadline (and only it) fires when the test says so.</summary>
    private sealed class BudgetClock : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<BudgetTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != TimeSpan.FromSeconds(BudgetSeconds))
            {
                return base.CreateTimer(callback, state, dueTime, period);
            }

            var timer = new BudgetTimer(this, callback, state);
            lock (_lock)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void ExpireBudgets()
        {
            BudgetTimer[] due;
            lock (_lock)
            {
                due = _timers.ToArray();
                _timers.Clear();
            }

            foreach (BudgetTimer timer in due)
            {
                timer.Fire();
            }
        }

        private void Remove(BudgetTimer timer)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class BudgetTimer(BudgetClock owner, TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class AlwaysOptimizeGate : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Json(new JsonObject
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
    }

    /// <summary>Rewrites every message, except that the first call for <c>beta()</c> stalls until it is cancelled.</summary>
    private sealed class StallingOptimizer : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string userText = JsonNode.Parse(body)!["messages"]![1]!["content"]!.GetValue<string>();
            if (userText.Contains("beta()", StringComparison.Ordinal) && Stalled.TrySetResult())
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }

            return Json(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["content"] = userText
                            .Replace("<message_to_rewrite>\n", string.Empty, StringComparison.Ordinal)
                            .Replace("\n</message_to_rewrite>", string.Empty, StringComparison.Ordinal)
                            .Replace("Please kindly review", "Review", StringComparison.Ordinal)
                    }
                })
            });
        }
    }

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private sealed record LogLine(string Category, LogLevel Level, string Text);

    private sealed class LogCollector : ILoggerProvider
    {
        private readonly List<LogLine> _lines = [];

        public LogLine[] ApplicationLines()
        {
            lock (_lines)
            {
                return _lines
                    .Where(line => line.Category.StartsWith("SmoothLlmImposter.Application", StringComparison.Ordinal))
                    .ToArray();
            }
        }

        public string[] Information() => ApplicationLines()
            .Where(line => line.Level == LogLevel.Information)
            .Select(line => line.Text)
            .ToArray();

        public ILogger CreateLogger(string categoryName) => new Collecting(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Collecting(string category, List<LogLine> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(new LogLine(category, logLevel, formatter(state, exception)));
                }
            }
        }
    }

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
                    // UseSerilog bypasses extra providers, so swap the factory itself to see the routing loggers.
                    services.RemoveAll<ILoggerFactory>();
                    services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(logging =>
                        logging.SetMinimumLevel(LogLevel.Information).AddProvider(Logs)));
                    services.AddHttpClient("decision-service")
                        .ConfigurePrimaryHttpMessageHandler(() => Gate);
                    services.AddHttpClient("llm-messages")
                        .ConfigurePrimaryHttpMessageHandler(() => Optimizer);
                });
            });
            _client = _factory.CreateClient();
        }

        public BudgetClock Clock { get; } = new();

        public AlwaysOptimizeGate Gate { get; } = new();

        public StallingOptimizer Optimizer { get; } = new();

        public LogCollector Logs { get; } = new();

        public Task<HttpResponseMessage> SendAsync(JsonArray history, CancellationToken cancellationToken)
        {
            string body = new JsonObject
            {
                ["model"] = "anthropic-imposter",
                ["max_tokens"] = 100,
                ["messages"] = history.DeepClone()
            }.ToJsonString();
            var request = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", "sk-ant-test");
            request.Headers.Add("session_id", "budget-session");
            return _client.SendAsync(request, cancellationToken);
        }

        public JsonArray ForwardedMessages() =>
            JsonNode.Parse(_base.Upstream.LastRequestBody!)!["messages"]!.AsArray();

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
            ["Imposter:LlmMessages:TimeoutSeconds"] = "120",
            ["Imposter:LlmMessages:BudgetSeconds"] = BudgetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),

            ["Imposter:Decisions:BaseUrl"] = "https://gate.test",
            ["Imposter:Decisions:Path"] = "/v1/systemone",
            ["Imposter:Decisions:Model"] = "gate-model",
            ["Imposter:Decisions:MaxAttempts"] = "1",
            ["Imposter:Decisions:TimeoutSeconds"] = "120"
        };
    }
}
