extern alias HostApp;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// HLD 017 NFR-01: while a session's cache scope is retained, every message the stage has handled is forwarded
/// byte-identically on every later request, whatever happened to it on first handling. Each test resends the whole
/// history every turn, as a harness such as Claude Code does, and compares raw message bytes with the previous forward.
/// </summary>
public sealed class MessageOptimizerCachePrefixStabilityTests
{
    private const string Session = "session-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_handled_message_is_forwarded_byte_identically_across_gate_and_circuit_changes()
    {
        var gate = new ScriptedGate();
        var optimizer = new ScriptedOptimizer();
        using var app = new App(gate, optimizer);
        var conversation = new Conversation();
        string[] previous = [];

        // Turn 1: a new message is gated and rewritten.
        conversation.AddHuman("alpha");
        gate.Script(Step.Succeed);
        optimizer.Script(Step.Succeed);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((1, 1));

        // Turn 2: a tool-call continuation adds no human message, so nothing is asked of either model.
        conversation.AddToolRound("toolu_1");
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((1, 1));

        // Turn 3: the gate declines, so the original is what gets remembered.
        conversation.AddHuman("beta");
        gate.Script(Step.Decline);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((2, 1));

        // Turn 4: the optimizer answers with garbage. That failure does not open its circuit, so only the
        // remembered original keeps the message from being retried (and rewritten) once the optimizer recovers.
        conversation.AddHuman("theta");
        gate.Script(Step.Succeed);
        optimizer.Script(Step.Invalid);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((3, 2));

        // Turn 5: the optimizer's rewrite loses a protected code span, twice (the first answer and the one targeted
        // retry), so validation discards it and the original is what gets remembered.
        conversation.AddHuman("kappa");
        gate.Script(Step.Succeed);
        optimizer.Script(Step.BadRewrite);
        optimizer.Script(Step.BadRewrite);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((4, 4));

        // Turn 6: the gate fails mid-way (its circuit opens) and the optimizer still handles the message.
        conversation.AddHuman("gamma");
        gate.Script(Step.Fail);
        optimizer.Script(Step.Succeed);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((5, 5));

        // Turn 7: the gate circuit is open (no call) and the optimizer fails, which opens its circuit.
        conversation.AddHuman("delta");
        optimizer.Script(Step.Fail);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((5, 6));

        // Turn 8: both circuits are open, so neither model is called and the original is remembered.
        conversation.AddHuman("epsilon");
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((5, 6));

        // Turn 9: past the cooldown one trial call per section succeeds and both circuits close.
        app.Clock.Advance(TimeSpan.FromSeconds(61));
        conversation.AddHuman("zeta");
        gate.Script(Step.Succeed);
        optimizer.Script(Step.Succeed);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((6, 7));

        // Turn 10: fully healthy again; the closed circuits must not rewrite anything handled while they were open.
        conversation.AddHuman("eta");
        gate.Script(Step.Succeed);
        optimizer.Script(Step.Succeed);
        previous = AssertStable(previous, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((7, 8));

        conversation.ForwardedHumanTexts(previous).ShouldBe(
        [
            Rewritten("alpha"),
            Original("beta"),
            Original("theta"),
            Original("kappa"),
            Rewritten("gamma"),
            Original("delta"),
            Original("epsilon"),
            Rewritten("zeta"),
            Rewritten("eta")
        ]);
    }

    [Fact]
    public async Task A_scope_used_within_its_sliding_ttl_is_retained_and_one_idle_past_it_is_dropped()
    {
        var gate = new ScriptedGate();
        var optimizer = new ScriptedOptimizer();
        using var app = new App(gate, optimizer, cacheTtlHours: 1);
        var conversation = new Conversation();
        conversation.AddHuman("alpha");

        string[] first = await app.SendAsync(conversation, Session);
        Calls(gate, optimizer).ShouldBe((1, 1));

        // 40 minutes in, and again 40 minutes later: 80 minutes since the first request but only 40 since the
        // last use, so the sliding window keeps the scope and the message is replayed without a model call.
        app.Clock.Advance(TimeSpan.FromMinutes(40));
        string[] second = AssertStable(first, await app.SendAsync(conversation, Session));
        app.Clock.Advance(TimeSpan.FromMinutes(40));
        string[] third = AssertStable(second, await app.SendAsync(conversation, Session));
        Calls(gate, optimizer).ShouldBe((1, 1));

        // Idle past the TTL: the scope is dropped, so the message is handled again (the one accepted miss).
        app.Clock.Advance(TimeSpan.FromMinutes(61));
        string[] fourth = await app.SendAsync(conversation, Session);
        Calls(gate, optimizer).ShouldBe((2, 2));
        fourth.ShouldBe(third);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sk-ant-caller-key")]
    public async Task Requests_without_a_captured_session_are_optimized_every_time_and_never_cached(string? apiKey)
    {
        // No caller identity resolves to no session; an x-api-key alone resolves to a derived one. Neither is captured.
        var gate = new ScriptedGate();
        var optimizer = new ScriptedOptimizer();
        using var app = new App(gate, optimizer);
        var conversation = new Conversation();
        conversation.AddHuman("alpha");

        string[] first = await app.SendAsync(conversation, sessionId: null, apiKey);
        string[] second = await app.SendAsync(conversation, sessionId: null, apiKey);

        Calls(gate, optimizer).ShouldBe((2, 2));
        conversation.ForwardedHumanTexts(first).ShouldBe([Rewritten("alpha")]);
        conversation.ForwardedHumanTexts(second).ShouldBe([Rewritten("alpha")]);
    }

    private static (int Gate, int Optimizer) Calls(ScriptedGate gate, ScriptedOptimizer optimizer) =>
        (gate.Calls, optimizer.Calls);

    private static string Original(string code) => $"Please kindly review `{code}()` when you have a moment.";

    private static string Rewritten(string code) => $"Review `{code}()`.";

    /// <summary>Every message the previous forward carried must be forwarded again with the same bytes.</summary>
    private static string[] AssertStable(string[] previous, string[] current)
    {
        current.Length.ShouldBeGreaterThanOrEqualTo(previous.Length);
        for (int index = 0; index < previous.Length; index++)
        {
            current[index].ShouldBe(previous[index], $"message {index} changed between forwards");
        }

        return current;
    }

    private enum Step
    {
        Succeed,
        Decline,
        Fail,

        /// <summary>Optimizer only: a 200 whose envelope is unusable, a failure that never opens the circuit.</summary>
        Invalid,

        /// <summary>Optimizer only: a usable answer that drops the message's code span, so validation rejects it.</summary>
        BadRewrite
    }

    /// <summary>A Claude Code-shaped Anthropic history: a harness block plus the human text, tool rounds in between.</summary>
    private sealed class Conversation
    {
        private readonly List<JsonObject> _messages = [];

        public void AddHuman(string code) => _messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"<system-reminder>Project context for {code}.</system-reminder>"
                },
                new JsonObject { ["type"] = "text", ["text"] = Original(code) })
        });

        public void AddToolRound(string toolUseId)
        {
            _messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = toolUseId,
                    ["name"] = "Bash",
                    ["input"] = new JsonObject { ["command"] = "ls" }
                })
            });
            _messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = "ok"
                })
            });
        }

        public string ToBody() => new JsonObject
        {
            ["model"] = "anthropic-imposter",
            ["max_tokens"] = 100,
            ["messages"] = new JsonArray(_messages.Select(message => message.DeepClone()).ToArray())
        }.ToJsonString();

        /// <summary>The human text block of every human message in a forwarded history, in order.</summary>
        public string[] ForwardedHumanTexts(string[] forwarded) => forwarded
            .Select(raw => JsonNode.Parse(raw)!.AsObject())
            .Where(message => message["content"] is JsonArray blocks &&
                blocks.Count == 2 &&
                blocks[0]!["text"]!.GetValue<string>().StartsWith("<system-reminder>", StringComparison.Ordinal))
            .Select(message => message["content"]![1]!["text"]!.GetValue<string>())
            .ToArray();
    }

    private sealed class App : IDisposable
    {
        private readonly ImposterAppFixture _base = new();
        private readonly WebApplicationFactory<HostApp::Program> _factory;

        public App(ScriptedGate gate, ScriptedOptimizer optimizer, int cacheTtlHours = 120)
        {
            _factory = _base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.Sources.Clear();
                    config.AddInMemoryCollection(Config(cacheTtlHours));
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDecisionModelClient>();
                    services.AddSingleton<IDecisionModelClient>(gate);
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                    services.AddHttpClient("llm-messages")
                        .ConfigurePrimaryHttpMessageHandler(() => optimizer);
                });
            });
            Client = _factory.CreateClient();
        }

        public ManualTimeProvider Clock { get; } = new();

        private HttpClient Client { get; }

        /// <summary>Sends the whole history and returns the forwarded body's messages as raw JSON text.</summary>
        public async Task<string[]> SendAsync(Conversation conversation, string? sessionId, string? apiKey = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages")
            {
                Content = new StringContent(conversation.ToBody(), Encoding.UTF8, "application/json")
            };
            if (sessionId is not null)
            {
                request.Headers.Add("session_id", sessionId);
            }

            if (apiKey is not null)
            {
                request.Headers.Add("x-api-key", apiKey);
            }

            using HttpResponseMessage response = await Client.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

            using JsonDocument forwarded = JsonDocument.Parse(_base.Upstream.LastRequestBody!);
            return forwarded.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetRawText())
                .ToArray();
        }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            _base.Dispose();
        }

        private static Dictionary<string, string?> Config(int cacheTtlHours) => new()
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
            ["Imposter:LlmMessages:TimeoutSeconds"] = "10",
            ["Imposter:LlmMessages:CircuitBreakSeconds"] = "60",
            ["Imposter:LlmMessages:CacheTtlHours"] = cacheTtlHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Imposter:DecisionsMessages:CircuitBreakSeconds"] = "60"
        };
    }

    /// <summary>The scripted outcomes of one stub, consumed per call; an unscripted call succeeds.</summary>
    private sealed class StepScript
    {
        private readonly Queue<Step> _steps = new();

        public int Calls { get; private set; }

        public void Add(Step step) => _steps.Enqueue(step);

        public Step Next()
        {
            Calls++;
            return _steps.Count > 0 ? _steps.Dequeue() : Step.Succeed;
        }
    }

    private sealed class ScriptedGate : IDecisionModelClient
    {
        private readonly StepScript _script = new();

        public int Calls => _script.Calls;

        public void Script(Step step) => _script.Add(step);

        public Task<JsonObject> DecideAsync(string section, JsonObject request, CancellationToken cancellationToken)
        {
            Step step = _script.Next();
            if (step == Step.Fail)
            {
                throw new HttpRequestException("gate down", inner: null, HttpStatusCode.InternalServerError);
            }

            double optimize = step == Step.Succeed ? 0.9 : 0.1;
            return Task.FromResult(new JsonObject
            {
                ["answers"] = new JsonObject
                {
                    ["optimize"] = new JsonObject
                    {
                        ["probabilities"] = new JsonObject { ["optimize"] = optimize, ["keep"] = 1 - optimize }
                    }
                }
            });
        }
    }

    private sealed class ScriptedOptimizer : HttpMessageHandler
    {
        private static readonly Regex CodeSpan = new("`([a-z]+)\\(\\)`", RegexOptions.CultureInvariant);

        private readonly StepScript _script = new();

        public int Calls => _script.Calls;

        public void Script(Step step) => _script.Add(step);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Step step = _script.Next();
            if (step == Step.Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (step == Step.Invalid)
            {
                return Answer("""{"choices":[]}""");
            }

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string userText = JsonNode.Parse(body)!["messages"]![1]!["content"]!.GetValue<string>();
            string code = CodeSpan.Match(userText).Groups[1].Value;
            string content = step == Step.BadRewrite ? "Review it." : Rewritten(code);
            return Answer(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = content }
                })
            }.ToJsonString());
        }

        private static HttpResponseMessage Answer(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
