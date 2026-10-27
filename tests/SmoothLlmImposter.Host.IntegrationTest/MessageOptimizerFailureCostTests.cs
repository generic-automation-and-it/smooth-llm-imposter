extern alias HostApp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// HLD 017 NFR-03: a failing gate or optimizer costs at most one failed call per section per cooldown and never
/// changes what the caller sees. The real decision and optimizer clients and their resilience pipelines run; only
/// their transports are stubbed, and a manual clock stands in for the cooldown.
/// </summary>
public sealed class MessageOptimizerFailureCostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Behavior
    {
        Healthy,

        /// <summary>The service answers 500.</summary>
        Down,

        /// <summary>The transport throws before any response.</summary>
        Unreachable,

        /// <summary>The call hangs until its cancellation token fires.</summary>
        Stalled
    }

    public enum Section
    {
        Gate,
        Optimizer
    }

    [Theory]
    [InlineData(Behavior.Healthy, Behavior.Healthy, true)]
    [InlineData(Behavior.Down, Behavior.Healthy, true)]
    [InlineData(Behavior.Healthy, Behavior.Down, false)]
    [InlineData(Behavior.Down, Behavior.Down, false)]
    [InlineData(Behavior.Unreachable, Behavior.Healthy, true)]
    [InlineData(Behavior.Healthy, Behavior.Unreachable, false)]
    [InlineData(Behavior.Unreachable, Behavior.Unreachable, false)]
    public async Task Every_failure_combination_forwards_a_valid_body_and_the_caller_sees_an_ordinary_response(
        Behavior gate, Behavior optimizer, bool expectRewrite)
    {
        using var app = new App();
        app.Gate.Mode = gate;
        app.Optimizer.Mode = optimizer;

        using HttpResponseMessage response = await app.SendAsync(Body("alpha"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("""{"ok":true}""");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        app.Upstream.RequestCount.ShouldBe(1);
        JsonNode forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!;
        forwarded["model"]!.GetValue<string>().ShouldBe("upstream-model");
        forwarded["messages"]!.AsArray().Count.ShouldBe(1);
        forwarded["messages"]![0]!["content"]!.GetValue<string>()
            .ShouldBe(expectRewrite ? Rewritten("alpha") : Original("alpha"));

        // A down gate still leaves the optimizer to be asked, and a down optimizer is asked exactly once.
        (app.Gate.Calls, app.Optimizer.Calls).ShouldBe((1, 1));
    }

    [Theory]
    [InlineData(Section.Gate)]
    [InlineData(Section.Optimizer)]
    public async Task A_failed_section_makes_no_call_until_its_cooldown_ends_and_then_one_trial_decides(Section failing)
    {
        using var app = new App();
        Endpoint failingEndpoint = app.Endpoint(failing);
        Endpoint otherEndpoint = app.Endpoint(failing == Section.Gate ? Section.Optimizer : Section.Gate);
        failingEndpoint.Mode = Behavior.Down;
        var forwarded = new List<string>();

        async Task Turn(string code, int failingCalls, int otherCalls)
        {
            forwarded.Add(await app.ForwardedTextAsync(code));
            (failingEndpoint.Calls, otherEndpoint.Calls).ShouldBe(
                (failingCalls, otherCalls), $"after the request for '{code}'");
        }

        // One exhausted call opens the circuit; the other section is unaffected and keeps being asked.
        await Turn("one", failingCalls: 1, otherCalls: 1);
        await Turn("two", failingCalls: 1, otherCalls: 2);
        await Turn("three", failingCalls: 1, otherCalls: 3);

        // One second before the cooldown ends the circuit is still open.
        app.Clock.Advance(TimeSpan.FromSeconds(59));
        await Turn("four", failingCalls: 1, otherCalls: 4);

        // Past the cooldown exactly one trial call is made; it fails and reopens the circuit.
        app.Clock.Advance(TimeSpan.FromSeconds(2));
        await Turn("five", failingCalls: 2, otherCalls: 5);
        await Turn("six", failingCalls: 2, otherCalls: 6);

        // The next trial succeeds and closes it, so the section is called normally again.
        app.Clock.Advance(TimeSpan.FromSeconds(61));
        failingEndpoint.Mode = Behavior.Healthy;
        await Turn("seven", failingCalls: 3, otherCalls: 7);
        await Turn("eight", failingCalls: 4, otherCalls: 8);

        // A failed gate never stops the optimizer, so every message is rewritten; an optimizer that is down
        // forwards the original until its trial succeeds.
        string[] codes = ["one", "two", "three", "four", "five", "six", "seven", "eight"];
        string[] expected = failing == Section.Gate
            ? codes.Select(Rewritten).ToArray()
            : codes.Select((code, index) => index < 6 ? Original(code) : Rewritten(code)).ToArray();
        forwarded.ShouldBe(expected);
    }

    [Theory]
    [InlineData(Section.Gate, "message-gate", "gate-model", "https://gate.test/v1/systemone", Behavior.Down, "HTTP 500")]
    [InlineData(Section.Gate, "message-gate", "gate-model", "https://gate.test/v1/systemone", Behavior.Unreachable, "HttpRequestException")]
    [InlineData(Section.Optimizer, "message-optimizer", "opt-model", "https://opt.test/v1/chat/completions", Behavior.Down, "HTTP 500")]
    [InlineData(Section.Optimizer, "message-optimizer", "opt-model", "https://opt.test/v1/chat/completions", Behavior.Unreachable, "HttpRequestException")]
    public async Task Opening_a_circuit_logs_one_error_with_its_context_and_never_the_message_text(
        Section failing, string section, string model, string endpoint, Behavior failure, string cause)
    {
        using var app = new App();
        app.Endpoint(failing).Mode = failure;

        // Each message text carries a distinctive word that must never reach a log line.
        await app.ForwardedTextAsync("zebrafish");
        await app.ForwardedTextAsync("narwhal");
        await app.ForwardedTextAsync("axolotl");

        LogLine[] errors = app.Logs.RoutingErrors();
        errors.Length.ShouldBe(1, "only the call that opens the circuit is an error; the open circuit makes none");
        string text = errors[0].Text;
        text.ShouldContain(section);
        text.ShouldContain(model);
        text.ShouldContain(endpoint);
        text.ShouldContain(cause);
        text.ShouldContain("no call until");

        // No line carries message text, and no routing line passes an exception object that could.
        foreach (LogLine line in app.Logs.Lines)
        {
            line.Text.ShouldNotContain("zebrafish");
            line.Text.ShouldNotContain("narwhal");
            line.Text.ShouldNotContain("axolotl");
        }

        app.Logs.RoutingLines().ShouldAllBe(line => !line.HasException);

        // A repeat request through the open circuit adds no error and no call.
        int calls = app.Endpoint(failing).Calls;
        await app.ForwardedTextAsync("kraken");
        app.Endpoint(failing).Calls.ShouldBe(calls);
        app.Logs.RoutingErrors().Length.ShouldBe(1);
    }

    [Theory]
    [InlineData(Behavior.Healthy, Behavior.Healthy, Behavior.Healthy, true, true)]
    [InlineData(Behavior.Down, Behavior.Healthy, Behavior.Healthy, false, true)]
    [InlineData(Behavior.Healthy, Behavior.Down, Behavior.Down, true, false)]
    public async Task One_branch_failing_alone_leaves_the_other_branchs_edit_applied(
        Behavior tools, Behavior gate, Behavior optimizer, bool expectToolsPruned, bool expectRewrite)
    {
        using var app = new App(toolsEnabled: true);
        app.Tools.Mode = tools;
        app.Gate.Mode = gate;
        app.Optimizer.Mode = optimizer;

        using HttpResponseMessage response = await app.SendAsync(Body("alpha", withTools: true), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!;
        string[] toolNames = forwarded["tools"]!.AsArray()
            .Select(tool => tool!["function"]!["name"]!.GetValue<string>())
            .ToArray();
        toolNames.ShouldBe(expectToolsPruned ? ["read_file"] : ["read_file", "web_search"]);
        forwarded["messages"]![0]!["content"]!.GetValue<string>()
            .ShouldBe(expectRewrite ? Rewritten("alpha") : Original("alpha"));
        app.Tools.Calls.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(Section.Gate)]
    [InlineData(Section.Optimizer)]
    public async Task Caller_cancellation_during_a_call_opens_no_circuit(Section stalled)
    {
        using var app = new App();
        Endpoint endpoint = app.Endpoint(stalled);
        endpoint.Mode = Behavior.Stalled;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<HttpResponseMessage> pending = app.SendAsync(Body("alpha"), cancel.Token);
        await endpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        await endpoint.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        endpoint.Mode = Behavior.Healthy;
        (await app.ForwardedTextAsync("beta")).ShouldBe(Rewritten("beta"));

        endpoint.Calls.ShouldBe(2, "the cancelled call must not have opened the circuit");
        app.Logs.RoutingErrors().ShouldBeEmpty();
    }

    private static string Original(string code) => $"Please kindly review `{code}()` when you have a moment.";

    private static string Rewritten(string code) => $"Review `{code}()`.";

    private static string Body(string code, bool withTools = false)
    {
        var body = new JsonObject
        {
            ["model"] = "chat-imposter",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = Original(code) })
        };
        if (withTools)
        {
            body["tools"] = new JsonArray(Tool("read_file", "Read a local file"), Tool("web_search", "Search the web"));
        }

        return body.ToJsonString();
    }

    private static JsonObject Tool(string name, string description) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object" }
        }
    };

    /// <summary>A stubbed service: counts calls and fails on demand.</summary>
    private sealed class Endpoint
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Behavior Mode { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Counts the call, then returns the failure response, or null when the call should succeed.</summary>
        public async Task<HttpResponseMessage?> FaultAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            switch (Mode)
            {
                case Behavior.Down:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                case Behavior.Unreachable:
                    throw new HttpRequestException("connection refused");
                case Behavior.Stalled:
                    Started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Cancelled.TrySetResult();
                        throw;
                    }

                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary>The shared decision transport: the message gate and the tool classifier call the same URL.</summary>
    private sealed class DecisionHandler(Endpoint gate, Endpoint tools) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            JsonObject questions = body["questions"]!.AsObject();
            bool isGate = questions.ContainsKey("optimize");

            HttpResponseMessage? fault = await (isGate ? gate : tools).FaultAsync(cancellationToken);
            if (fault is not null)
            {
                return fault;
            }

            var answers = new JsonObject();
            foreach ((string key, JsonNode? question) in questions)
            {
                answers[key] = isGate
                    ? new JsonObject { ["probabilities"] = new JsonObject { ["optimize"] = 0.9, ["keep"] = 0.1 } }
                    : new JsonObject { ["probabilities"] = ToolScore(question!["instructions"]!.GetValue<string>()) };
            }

            return Json(new JsonObject { ["answers"] = answers });
        }

        private static JsonObject ToolScore(string instructions)
        {
            double use = instructions.Contains("read_file", StringComparison.Ordinal) ? 0.9 : 0.1;
            return new JsonObject { ["use"] = use, ["skip"] = 1 - use };
        }
    }

    private sealed class OptimizerHandler(Endpoint optimizer) : HttpMessageHandler
    {
        private static readonly Regex CodeSpan = new("`([a-z]+)\\(\\)`", RegexOptions.CultureInvariant);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage? fault = await optimizer.FaultAsync(cancellationToken);
            if (fault is not null)
            {
                return fault;
            }

            JsonNode sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            string userText = sent["messages"]![1]!["content"]!.GetValue<string>();
            string content = Rewritten(CodeSpan.Match(userText).Groups[1].Value);
            return Json(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = content }
                })
            });
        }
    }

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private sealed record LogLine(string Category, LogLevel Level, string Text, bool HasException);

    private sealed class LogCollector : ILoggerProvider
    {
        private readonly List<LogLine> _lines = [];

        public IReadOnlyList<LogLine> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public LogLine[] RoutingLines() => Lines
            .Where(line => line.Category.StartsWith("SmoothLlmImposter.Application", StringComparison.Ordinal))
            .ToArray();

        public LogLine[] RoutingErrors() => RoutingLines().Where(line => line.Level >= LogLevel.Error).ToArray();

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
                    lines.Add(new LogLine(category, logLevel, formatter(state, exception), exception is not null));
                }
            }
        }
    }

    private sealed class App : IDisposable
    {
        private readonly ImposterAppFixture _base = new();
        private readonly WebApplicationFactory<HostApp::Program> _factory;
        private readonly HttpClient _client;

        public App(bool toolsEnabled = false)
        {
            Gate = new Endpoint();
            Tools = new Endpoint();
            Optimizer = new Endpoint();
            Logs = new LogCollector();
            _factory = _base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.Sources.Clear();
                    config.AddInMemoryCollection(Config(toolsEnabled));
                });
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<TimeProvider>(Clock);
                    // UseSerilog bypasses extra providers, so swap the factory itself to see the routing loggers.
                    // Information is the Host's default level; the Debug request dump is an operator opt-in.
                    services.RemoveAll<ILoggerFactory>();
                    services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(logging =>
                        logging.SetMinimumLevel(LogLevel.Information).AddProvider(Logs)));
                    services.AddHttpClient("decision-service")
                        .ConfigurePrimaryHttpMessageHandler(() => new DecisionHandler(Gate, Tools));
                    services.AddHttpClient("llm-messages")
                        .ConfigurePrimaryHttpMessageHandler(() => new OptimizerHandler(Optimizer));
                });
            });
            _client = _factory.CreateClient();
        }

        public ManualTimeProvider Clock { get; } = new();

        public Endpoint Gate { get; }

        public Endpoint Tools { get; }

        public Endpoint Optimizer { get; }

        public LogCollector Logs { get; }

        public StubUpstreamHandler Upstream => _base.Upstream;

        public Endpoint Endpoint(Section section) => section == Section.Gate ? Gate : Optimizer;

        public Task<HttpResponseMessage> SendAsync(string body, CancellationToken cancellationToken) =>
            _client.PostAsync(
                "/openai/v1/chat/completions",
                new StringContent(body, Encoding.UTF8, "application/json"),
                cancellationToken);

        /// <summary>Sends one message and returns the text the upstream received for it.</summary>
        public async Task<string> ForwardedTextAsync(string code)
        {
            using HttpResponseMessage response = await SendAsync(Body(code), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return JsonNode.Parse(Upstream.LastRequestBody!)!["messages"]![0]!["content"]!.GetValue<string>();
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
            _base.Dispose();
        }

        private static Dictionary<string, string?> Config(bool toolsEnabled) => new()
        {
            ["Imposter:Providers:openai-official:Dialect"] = "openai",
            ["Imposter:Providers:openai-official:BaseUrl"] = "https://official.test",
            ["Imposter:Providers:openai-official:IsDefault"] = "true",

            ["Imposter:Providers:chat-mapped:Dialect"] = "openai",
            ["Imposter:Providers:chat-mapped:BaseUrl"] = "https://imposter.test",
            ["Imposter:Providers:chat-mapped:Models:0:From"] = "chat-imposter",
            ["Imposter:Providers:chat-mapped:Models:0:To"] = "upstream-model",

            ["Imposter:LlmMessages:Enabled"] = "true",
            ["Imposter:LlmMessages:BaseUrl"] = "https://opt.test",
            ["Imposter:LlmMessages:Model"] = "opt-model",
            ["Imposter:LlmMessages:MaxAttempts"] = "1",
            ["Imposter:LlmMessages:TimeoutSeconds"] = "10",
            ["Imposter:LlmMessages:CircuitBreakSeconds"] = "60",
            ["Imposter:DecisionsMessages:CircuitBreakSeconds"] = "60",

            ["Imposter:DecisionsTools:Enabled"] = toolsEnabled.ToString(),
            ["Imposter:DecisionsTools:BaseUrl"] = "https://gate.test",
            ["Imposter:DecisionsTools:Path"] = "/v1/systemone",
            ["Imposter:DecisionsTools:Model"] = "gate-model",
            ["Imposter:DecisionsTools:MaxAttempts"] = "1",
            ["Imposter:DecisionsTools:TimeoutSeconds"] = "10",
            ["Imposter:DecisionsTools:MinKeepTools"] = "0",
            ["Imposter:DecisionsTools:AlwaysKeep"] = "none-configured"
        };
    }
}
