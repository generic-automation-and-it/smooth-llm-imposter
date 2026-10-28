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
/// HLD 017 NFR-04: user text leaves the process only to the configured gate and optimizer endpoints, only as a
/// candidate's human spans, one message at a time, and never into a log. Every request the stubbed gate and optimizer
/// transports receive is captured for a Claude Code-shaped turn whose non-human content carries a distinctive sentinel.
/// </summary>
public sealed class MessageOptimizerEgressTests
{
    private const string FirstHuman = "Please kindly review `alpha()` when you have a moment.";
    private const string SecondHuman = "Please kindly review `beta()` when you can.";
    private const string CallerKey = "sk-ant-CALLERKEY-SENTINEL";

    /// <summary>Content the harness, the assistant or a tool produced; none of it may reach either model.</summary>
    private static readonly string[] NonHumanSentinels =
    [
        "SYSTEMPROMPT-SENTINEL",
        "TOOLDEF-SENTINEL",
        "REMINDER-SENTINEL",
        "HOOK-SENTINEL",
        "COMMANDARGS-SENTINEL",
        "LOCALOUT-SENTINEL",
        "INLINE-SENTINEL",
        "ASSISTANT-SENTINEL",
        "ASSISTANTTWO-SENTINEL",
        "TOOLINPUT-SENTINEL",
        "TOOLRESULT-SENTINEL",
        "IMAGE-SENTINEL"
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_the_human_spans_of_each_candidate_reach_the_gate_and_the_optimizer()
    {
        using var app = new App();

        using HttpResponseMessage response = await app.SendAsync(ClaudeCodeTurn(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Models.Requests.Count.ShouldBe(4);
        string[] gateStates = app.Models.Of(ModelKind.Gate).Select(request => request.Json["state"]!.GetValue<string>()).ToArray();
        string[] optimizerTexts = app.Models.Of(ModelKind.Optimizer)
            .Select(request => request.Json["messages"]![1]!["content"]!.GetValue<string>())
            .ToArray();
        gateStates.ShouldBe([FirstHuman, SecondHuman]);
        optimizerTexts.ShouldBe([Delimited(FirstHuman), Delimited(SecondHuman)]);

        foreach (ModelRequest request in app.Models.Requests)
        {
            foreach (string sentinel in NonHumanSentinels.Append(CallerKey))
            {
                request.Raw.ShouldNotContain(sentinel, customMessage: $"{request.Kind} request leaked {sentinel}");
            }
        }

        // The withheld content is still part of the request the upstream receives, untouched.
        JsonNode forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!;
        foreach (string sentinel in NonHumanSentinels)
        {
            app.Upstream.LastRequestBody!.ShouldContain(sentinel);
        }

        forwarded["messages"]![0]!["content"]![1]!["text"]!.GetValue<string>().ShouldBe("Review `alpha()`.");
    }

    [Fact]
    public async Task Each_optimizer_request_carries_only_instructions_and_one_user_message()
    {
        using var app = new App();

        await app.SendAsync(ClaudeCodeTurn(), Ct);

        ModelRequest[] optimizerRequests = app.Models.Of(ModelKind.Optimizer);
        optimizerRequests.Length.ShouldBe(2);
        foreach (ModelRequest request in optimizerRequests)
        {
            request.Json.AsObject().Select(property => property.Key).Order().ToArray()
                .ShouldBe(["messages", "model", "stream"]);
            JsonArray messages = request.Json["messages"]!.AsArray();
            messages.Select(message => message!["role"]!.GetValue<string>()).ToArray().ShouldBe(["system", "user"]);
            messages[1]!["content"]!.ShouldBeAssignableTo<JsonValue>("the user message is plain text, never a content array");
            messages[0]!["content"]!.GetValue<string>().ShouldNotContain(FirstHuman);
            messages[0]!["content"]!.GetValue<string>().ShouldNotContain(SecondHuman);
        }

        foreach (ModelRequest request in app.Models.Of(ModelKind.Gate))
        {
            request.Json.AsObject().Select(property => property.Key).Order().ToArray()
                .ShouldBe(["model", "questions", "state"]);
            request.Json["model"]!.GetValue<string>().ShouldBe("gate-model");
            request.Json["questions"]!.AsObject().Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Two_candidates_are_sent_as_separate_requests_one_after_the_other()
    {
        using var app = new App();

        await app.SendAsync(ClaudeCodeTurn(), Ct);

        // Gated once per message, then optimized, then the next message: never batched and never in parallel.
        app.Models.Requests.Select(request => request.Kind).ToArray()
            .ShouldBe([ModelKind.Gate, ModelKind.Optimizer, ModelKind.Gate, ModelKind.Optimizer]);
        app.Models.MaxInFlight.ShouldBe(1);
        app.Models.Requests[0].Raw.ShouldNotContain("beta()");
        app.Models.Requests[1].Raw.ShouldNotContain("beta()");
        app.Models.Requests[2].Raw.ShouldNotContain("alpha()");
        app.Models.Requests[3].Raw.ShouldNotContain("alpha()");
    }

    [Fact]
    public async Task No_log_line_at_any_level_carries_message_text_apart_from_the_opt_in_inbound_dump()
    {
        using var app = new App();

        await app.SendAsync(ClaudeCodeTurn(), Ct);

        string[] messageText = [FirstHuman, SecondHuman, "alpha()", "beta()", "Review `", .. NonHumanSentinels];
        LogLine[] leaking = app.Logs.Lines
            .Where(line => messageText.Any(text => line.Text.Contains(text, StringComparison.Ordinal)))
            .ToArray();

        // The routing layer's Debug request dump logs the raw inbound body by design (operator opt-in, off at the
        // Host's default level). It is the one known exception, pinned so any new leaking line fails this test.
        leaking.Select(line => line.Text.Split('\n')[0]).ShouldBe(["Inbound Anthropic request POST /v1/messages"]);

        // Non-vacuous: the stage did log its outcome and sizes at Debug, just without the text.
        app.Logs.Lines.ShouldContain(line =>
            line.Category.EndsWith("MessageOptimizationStrategy", StringComparison.Ordinal) &&
            line.Text.StartsWith("Message optimization: rewritten (", StringComparison.Ordinal));

        // A logged exception object could carry message text in its own message, so the stage passes none.
        app.Logs.Lines
            .Where(line => line.Category.StartsWith("SmoothLlmImposter.Application", StringComparison.Ordinal))
            .ShouldAllBe(line => !line.HasException);
    }

    [Fact]
    public async Task A_disabled_feature_sends_nothing_to_either_endpoint()
    {
        using var app = new App(enabled: false);

        using HttpResponseMessage response = await app.SendAsync(ClaudeCodeTurn(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Models.Requests.ShouldBeEmpty();
        JsonNode sent = JsonNode.Parse(ClaudeCodeTurn())!;
        JsonNode forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!;
        JsonNode.DeepEquals(forwarded["messages"], sent["messages"]).ShouldBeTrue("messages are forwarded unchanged");
    }

    /// <summary>
    /// A Claude Code-shaped Anthropic request: a system prompt and tools, then two human messages separated by an
    /// assistant tool round, with reminders, hook output, a skill command block, local-command output and an image.
    /// </summary>
    private static string ClaudeCodeTurn() => new JsonObject
    {
        ["model"] = "anthropic-imposter",
        ["max_tokens"] = 100,
        ["system"] = new JsonArray(Text("SYSTEMPROMPT-SENTINEL you are an assistant")),
        ["tools"] = new JsonArray(new JsonObject
        {
            ["name"] = "Bash",
            ["description"] = "TOOLDEF-SENTINEL runs a command",
            ["input_schema"] = new JsonObject { ["type"] = "object" }
        }),
        ["messages"] = new JsonArray(
            User(Text("<system-reminder>REMINDER-SENTINEL project context</system-reminder>"), Text(FirstHuman)),
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(
                    Text("ASSISTANT-SENTINEL I will start."),
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = "toolu_1",
                        ["name"] = "Bash",
                        ["input"] = new JsonObject { ["command"] = "TOOLINPUT-SENTINEL" }
                    })
            },
            User(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = "toolu_1",
                ["content"] = "TOOLRESULT-SENTINEL"
            }),
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(Text("ASSISTANTTWO-SENTINEL done."))
            },
            User(
                Text("<system-reminder>UserPromptSubmit hook success: HOOK-SENTINEL</system-reminder>"),
                Text("<command-name>/review</command-name>\n<command-args>COMMANDARGS-SENTINEL</command-args>\n" +
                    "<local-command-stdout>LOCALOUT-SENTINEL</local-command-stdout>"),
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = "IMAGE-SENTINEL"
                    }
                },
                Text(SecondHuman + "<system-reminder>INLINE-SENTINEL</system-reminder>")))
    }.ToJsonString();

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject User(params JsonNode[] blocks) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray(blocks.Select(block => block.DeepClone()).ToArray())
    };

    private enum ModelKind
    {
        Gate,
        Optimizer
    }

    private sealed record ModelRequest(ModelKind Kind, string Raw, JsonNode Json);

    /// <summary>Everything the two stubbed model transports received, in arrival order.</summary>
    private sealed class ModelTraffic
    {
        private readonly List<ModelRequest> _requests = [];
        private int _inFlight;
        private int _maxInFlight;

        public IReadOnlyList<ModelRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public ModelRequest[] Of(ModelKind kind) => Requests.Where(request => request.Kind == kind).ToArray();

        /// <summary>Records the request (headers and body) and holds it in flight across a few yields so overlap shows.</summary>
        public async Task<ModelRequest> RecordAsync(ModelKind kind, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string headers = string.Join('\n', request.Headers.Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));
            var captured = new ModelRequest(kind, headers + "\n" + body, JsonNode.Parse(body)!);
            lock (_requests)
            {
                _requests.Add(captured);
            }

            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)))
            {
                Interlocked.CompareExchange(ref _maxInFlight, now, seen);
            }

            for (int index = 0; index < 5; index++)
            {
                await Task.Yield();
            }

            Interlocked.Decrement(ref _inFlight);
            return captured;
        }
    }

    private sealed class GateHandler(ModelTraffic traffic) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await traffic.RecordAsync(ModelKind.Gate, request, cancellationToken);
            return Json(new JsonObject
            {
                ["answers"] = new JsonObject
                {
                    ["optimize"] = new JsonObject
                    {
                        ["probabilities"] = new JsonObject { ["optimize"] = 0.9, ["keep"] = 0.1 }
                    }
                }
            });
        }
    }

    private sealed class OptimizerHandler(ModelTraffic traffic) : HttpMessageHandler
    {
        private static readonly Regex CodeSpan = new("`([a-z]+)\\(\\)`", RegexOptions.CultureInvariant);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ModelRequest captured = await traffic.RecordAsync(ModelKind.Optimizer, request, cancellationToken);
            string userText = captured.Json["messages"]![1]!["content"]!.GetValue<string>();
            return Json(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = $"Review `{CodeSpan.Match(userText).Groups[1].Value}()`." }
                })
            });
        }
    }

    private static string Delimited(string text) => $"<message_to_rewrite>\n{text}\n</message_to_rewrite>";

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private sealed record LogLine(string Category, string Text, bool HasException);

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
                    lines.Add(new LogLine(category, formatter(state, exception), exception is not null));
                }
            }
        }
    }

    private sealed class App : IDisposable
    {
        private readonly ImposterAppFixture _base = new();
        private readonly WebApplicationFactory<HostApp::Program> _factory;
        private readonly HttpClient _client;

        public App(bool enabled = true)
        {
            _factory = _base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.Sources.Clear();
                    config.AddInMemoryCollection(Config(enabled));
                });
                builder.ConfigureServices(services =>
                {
                    // UseSerilog bypasses extra providers, so swap the factory itself; Trace captures every level.
                    services.RemoveAll<ILoggerFactory>();
                    services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(logging =>
                        logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs)));
                    services.AddHttpClient("decision-service")
                        .ConfigurePrimaryHttpMessageHandler(() => new GateHandler(Models));
                    services.AddHttpClient("llm-messages")
                        .ConfigurePrimaryHttpMessageHandler(() => new OptimizerHandler(Models));
                });
            });
            _client = _factory.CreateClient();
        }

        public ModelTraffic Models { get; } = new();

        public LogCollector Logs { get; } = new();

        public StubUpstreamHandler Upstream => _base.Upstream;

        public Task<HttpResponseMessage> SendAsync(string body, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", CallerKey);
            request.Headers.Add("session_id", "egress-session");
            return _client.SendAsync(request, cancellationToken);
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
            _base.Dispose();
        }

        private static Dictionary<string, string?> Config(bool enabled) => new()
        {
            ["Imposter:Providers:openai-official:Dialect"] = "openai",
            ["Imposter:Providers:openai-official:BaseUrl"] = "https://official.test",
            ["Imposter:Providers:openai-official:IsDefault"] = "true",

            ["Imposter:Providers:anthropic-mapped:Dialect"] = "anthropic",
            ["Imposter:Providers:anthropic-mapped:BaseUrl"] = "https://imposter.test",
            ["Imposter:Providers:anthropic-mapped:Models:0:From"] = "anthropic-imposter",
            ["Imposter:Providers:anthropic-mapped:Models:0:To"] = "upstream-model",

            ["Imposter:LlmMessages:Enabled"] = enabled.ToString(),
            ["Imposter:LlmMessages:BaseUrl"] = "https://opt.test",
            ["Imposter:LlmMessages:Model"] = "opt-model",
            ["Imposter:LlmMessages:MaxAttempts"] = "1",
            ["Imposter:LlmMessages:TimeoutSeconds"] = "10",

            ["Imposter:DecisionsTools:BaseUrl"] = "https://gate.test",
            ["Imposter:DecisionsTools:Path"] = "/v1/systemone",
            ["Imposter:DecisionsTools:Model"] = "gate-model",
            ["Imposter:DecisionsTools:MaxAttempts"] = "1",
            ["Imposter:DecisionsTools:TimeoutSeconds"] = "10"
        };
    }
}
