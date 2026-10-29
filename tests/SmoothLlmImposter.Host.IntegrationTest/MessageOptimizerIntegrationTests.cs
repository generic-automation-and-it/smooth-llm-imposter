extern alias HostApp;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Host.IntegrationTest;

public sealed class MessageOptimizerIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_enabled_optimizer_rewrites_the_forwarded_chat_message()
    {
        var gate = new StubDecisionClient();
        var optimizer = new OptimizerHandler();
        using var app = new App(gate, optimizer);
        const string body = """
            {"model":"chat-imposter","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}
            """;

        using HttpResponseMessage response = await app.Client.PostAsync(
            "/openai/v1/chat/completions", Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        gate.Requests.Single()["state"]!.GetValue<string>().ShouldBe("Please kindly review `foo()`.");
        optimizer.Uris.Single().ShouldBe(new Uri("https://opt.test/v1/chat/completions"));
        JsonObject optimizerRequest = optimizer.Requests.Single();
        optimizerRequest["model"]!.GetValue<string>().ShouldBe("opt-model");
        optimizerRequest["messages"]![1]!["content"]!.GetValue<string>()
            .ShouldBe("<message_to_rewrite>\nPlease kindly review `foo()`.\n</message_to_rewrite>");

        JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
        forwarded["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Review `foo()`.");
    }

    [Fact]
    public async Task An_opted_out_route_forwards_the_message_without_gate_or_optimizer_calls()
    {
        var gate = new StubDecisionClient();
        var optimizer = new OptimizerHandler();
        using var app = new App(gate, optimizer);
        const string body = """
            {"model":"optout-imposter","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}
            """;

        using HttpResponseMessage response = await app.Client.PostAsync(
            "/openai/v1/chat/completions", Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        gate.Requests.ShouldBeEmpty();
        optimizer.Requests.ShouldBeEmpty();
        JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
        forwarded["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Please kindly review `foo()`.");
    }

    [Fact]
    public async Task A_downgraded_responses_request_is_not_optimized()
    {
        var gate = new StubDecisionClient();
        var optimizer = new OptimizerHandler();
        using var app = new App(gate, optimizer);
        const string body = """{"model":"downgrade-imposter","input":"Please kindly review `foo()`."}""";

        using HttpResponseMessage response = await app.Client.PostAsync("/openai/v1/responses", Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        gate.Requests.ShouldBeEmpty();
        optimizer.Requests.ShouldBeEmpty();
        JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
        forwarded["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Please kindly review `foo()`.");
    }

    [Fact]
    public async Task An_enabled_optimizer_rewrites_an_anthropic_text_block()
    {
        var gate = new StubDecisionClient();
        var optimizer = new OptimizerHandler();
        using var app = new App(gate, optimizer);
        const string body = """
            {"model":"anthropic-imposter","max_tokens":100,"messages":[
              {"role":"user","content":[{"type":"text","text":"Please kindly review `foo()`."}]}]}
            """;

        using HttpResponseMessage response = await app.Client.PostAsync(
            "/anthropic/v1/messages", Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        gate.Requests.Count.ShouldBe(1);
        optimizer.Requests.Count.ShouldBe(1);
        JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
        forwarded["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Review `foo()`.");
    }

    [Fact]
    public async Task A_captured_session_replays_a_rewrite_without_new_model_calls()
    {
        var gate = new StubDecisionClient();
        var optimizer = new OptimizerHandler();
        using var app = new App(gate, optimizer);
        const string body = """{"model":"chat-imposter","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";

        using HttpResponseMessage first = await app.Client.SendAsync(
            Request("/openai/v1/chat/completions", body, "session-1"), Ct);
        using HttpResponseMessage second = await app.Client.SendAsync(
            Request("/openai/v1/chat/completions", body, "session-1"), Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        gate.Requests.Count.ShouldBe(1);
        optimizer.Requests.Count.ShouldBe(1);
        JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
        forwarded["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Review `foo()`.");
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Request(string path, string body, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        request.Headers.Add("session_id", sessionId);
        return request;
    }

    private sealed class App : IDisposable
    {
        private readonly ImposterAppFixture _base = new();
        private readonly WebApplicationFactory<HostApp::Program> _factory;

        public App(IDecisionModelClient decisions, OptimizerHandler optimizer)
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
                    services.RemoveAll<IDecisionModelClient>();
                    services.AddSingleton(decisions);
                    services.AddHttpClient("llm-messages")
                        .ConfigurePrimaryHttpMessageHandler(() => optimizer);
                });
            });
            Client = _factory.CreateClient();
        }

        public HttpClient Client { get; }
        public StubUpstreamHandler Upstream => _base.Upstream;

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            _base.Dispose();
        }

        private static Dictionary<string, string?> Config() => new()
        {
            ["Imposter:Providers:openai-official:Dialect"] = "openai",
            ["Imposter:Providers:openai-official:BaseUrl"] = "https://official.test",
            ["Imposter:Providers:openai-official:IsDefault"] = "true",

            ["Imposter:Providers:chat-mapped:Dialect"] = "openai",
            ["Imposter:Providers:chat-mapped:BaseUrl"] = "https://imposter.test",
            ["Imposter:Providers:chat-mapped:Models:0:From"] = "chat-imposter",
            ["Imposter:Providers:chat-mapped:Models:0:To"] = "upstream-model",

            ["Imposter:Providers:chat-optout:Dialect"] = "openai",
            ["Imposter:Providers:chat-optout:BaseUrl"] = "https://imposter.test",
            ["Imposter:Providers:chat-optout:OptimizeMessages"] = "false",
            ["Imposter:Providers:chat-optout:Models:0:From"] = "optout-imposter",
            ["Imposter:Providers:chat-optout:Models:0:To"] = "upstream-model",

            ["Imposter:Providers:downgrade-mapped:Dialect"] = "openai",
            ["Imposter:Providers:downgrade-mapped:BaseUrl"] = "https://imposter.test",
            ["Imposter:Providers:downgrade-mapped:OpenAiUpstreamApi"] = "chat_completions",
            ["Imposter:Providers:downgrade-mapped:Models:0:From"] = "downgrade-imposter",
            ["Imposter:Providers:downgrade-mapped:Models:0:To"] = "upstream-model",

            ["Imposter:Providers:anthropic-mapped:Dialect"] = "anthropic",
            ["Imposter:Providers:anthropic-mapped:BaseUrl"] = "https://imposter.test",
            ["Imposter:Providers:anthropic-mapped:Models:0:From"] = "anthropic-imposter",
            ["Imposter:Providers:anthropic-mapped:Models:0:To"] = "upstream-model",

            ["Imposter:LlmMessages:Enabled"] = "true",
            ["Imposter:LlmMessages:BaseUrl"] = "https://opt.test",
            ["Imposter:LlmMessages:Model"] = "opt-model",
            ["Imposter:LlmMessages:MaxAttempts"] = "1",
            ["Imposter:LlmMessages:TimeoutSeconds"] = "10"
        };
    }

    private sealed class StubDecisionClient : IDecisionModelClient
    {
        public ConcurrentQueue<JsonObject> Requests { get; } = new();

        public Task<JsonObject> DecideAsync(string section, JsonObject request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.DeepClone().AsObject());
            return Task.FromResult(new JsonObject
            {
                ["answers"] = new JsonObject
                {
                    ["optimize"] = new JsonObject
                    {
                        ["probabilities"] = new JsonObject
                        {
                            ["optimize"] = 0.9,
                            ["keep"] = 0.1
                        }
                    }
                }
            });
        }
    }

    private sealed class OptimizerHandler : HttpMessageHandler
    {
        public ConcurrentQueue<JsonObject> Requests { get; } = new();
        public ConcurrentQueue<Uri> Uris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(JsonNode.Parse(body)!.AsObject());
            Uris.Enqueue(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"Review `foo()`."}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
