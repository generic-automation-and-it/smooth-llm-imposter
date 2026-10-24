extern alias HostApp;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// L2 coverage for HLD 015: the real Host resolves the shared decision transport from the unchanged
/// <c>Imposter:DecisionsTools</c> surface, validates it only while a consumer is opted in, and serves the
/// tool filter through the real client, gate and resilience pipeline (only the transports are stubbed).
/// </summary>
public sealed class DecisionServiceIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enabled_tool_filter_calls_the_configured_decision_service_through_the_real_client()
    {
        var decisions = new DecisionServiceHandler();
        using var factory = new DecisionServiceFixture(decisions, new Dictionary<string, string?>
        {
            ["Imposter:DecisionsTools:Enabled"] = "true",
            ["Imposter:DecisionsTools:BaseUrl"] = "https://decisions.test/root/",
            ["Imposter:DecisionsTools:Path"] = "/v1/systemone",
            ["Imposter:DecisionsTools:Model"] = "configured-model",
        });
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent("""
            { "model": "gpt-test", "messages": [{"role":"user","content":"Read the file"}],
              "tools": [{"type":"function","function":{"name":"read_file","description":"Read a local file","parameters":{"type":"object"}}},
                        {"type":"function","function":{"name":"web_search","description":"Search the web","parameters":{"type":"object"}}}] }
            """, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        decisions.Uris.ShouldBe([new Uri("https://decisions.test/root/v1/systemone")]);
        JsonObject sent = decisions.Bodies.Single();
        sent["model"]!.GetValue<string>().ShouldBe("configured-model");
        JsonArray tools = JsonNode.Parse(factory.Upstream.LastRequestBody!)!["tools"]!.AsArray();
        tools.Select(static tool => tool!["function"]!["name"]!.GetValue<string>()).ShouldBe(["read_file"]);
    }

    [Theory]
    [InlineData("BaseUrl", "not-a-url")]
    [InlineData("Path", "relative")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("TimeoutSeconds", "0")]
    public void Enabled_tool_filter_fails_startup_on_invalid_transport_settings(string field, string value)
    {
        using var factory = new DecisionServiceFixture(new DecisionServiceHandler(), new Dictionary<string, string?>
        {
            ["Imposter:DecisionsTools:Enabled"] = "true",
            [$"Imposter:DecisionsTools:{field}"] = value,
        });

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Failures.ShouldContain(message => message.Contains($"Imposter:DecisionsTools:{field}"));
    }

    [Fact]
    public async Task Disabled_tool_filter_ignores_invalid_transport_settings_and_never_calls_the_service()
    {
        var decisions = new DecisionServiceHandler();
        using var factory = new DecisionServiceFixture(decisions, new Dictionary<string, string?>
        {
            ["Imposter:DecisionsTools:BaseUrl"] = "not-a-url",
            ["Imposter:DecisionsTools:Path"] = "relative",
            ["Imposter:DecisionsTools:MaxAttempts"] = "0",
            ["Imposter:DecisionsTools:TimeoutSeconds"] = "-1",
        });
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent("""
            { "model": "gpt-test", "messages": [{"role":"user","content":"Read the file"}],
              "tools": [{"type":"function","function":{"name":"read_file","parameters":{"type":"object"}}}] }
            """, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        decisions.Uris.ShouldBeEmpty();
    }

    private sealed class DecisionServiceHandler : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Uris { get; } = new();
        public ConcurrentQueue<JsonObject> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Enqueue(request.RequestUri!);
            JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Bodies.Enqueue(body);

            var answers = new JsonObject();
            foreach ((string key, JsonNode? question) in body["questions"]!.AsObject())
            {
                double use = question!["instructions"]!.GetValue<string>().Contains("read_file", StringComparison.Ordinal) ? 0.9 : 0.1;
                answers[key] = new JsonObject { ["probabilities"] = new JsonObject { ["use"] = use, ["skip"] = 1 - use } };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["answers"] = answers }.ToJsonString(), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class DecisionServiceFixture(
        DecisionServiceHandler decisions,
        Dictionary<string, string?> decisionConfig) : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var config = new Dictionary<string, string?>(decisionConfig)
            {
                ["Imposter:DecisionsTools:MinKeepTools"] = "0",
                ["Imposter:DecisionsTools:AlwaysKeep"] = "none-configured",
                ["Imposter:Providers:openai-official:Dialect"] = "openai",
                ["Imposter:Providers:openai-official:BaseUrl"] = "https://official.test",
                ["Imposter:Providers:openai-official:IsDefault"] = "true",
            };

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(config);
            });

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("imposter-upstream").ConfigurePrimaryHttpMessageHandler(() => Upstream);
                services.AddHttpClient("decision-service").ConfigurePrimaryHttpMessageHandler(() => decisions);
            });
        }
    }
}
