extern alias HostApp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// End-to-end HLD 016 coverage: a matched Anthropic imposter route opted into
/// <c>AnthropicUpstreamApi: chat_completions</c> maps Messages→Chat, forwards once with the header
/// allowlist, and maps the reply back; a native Messages provider stays byte-transparent.
/// </summary>
public sealed class AnthropicChatBridgeIntegrationTests
{
    private const string BridgeModel = "claude-bridge-sonnet";
    private const string NativeModel = "claude-native-sonnet";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bridged_non_stream_maps_request_and_reply()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () => Json(HttpStatusCode.OK,
            """{"id":"chatcmpl-1","model":"gpt-upstream","choices":[{"index":0,"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":10}}""");
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await PostMessages(client, BridgeModel, stream: false);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        JsonObject message = JsonNode.Parse(body)!.AsObject();
        message["type"]!.GetValue<string>().ShouldBe("message");
        message["model"]!.GetValue<string>().ShouldBe(BridgeModel);
        message["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Hello");
        message["stop_reason"]!.GetValue<string>().ShouldBe("end_turn");
        message["usage"]!["input_tokens"]!.GetValue<long>().ShouldBe(100);

        fixture.Upstream.LastRequestUri!.AbsolutePath.ShouldBe("/v1/chat/completions");
        JsonObject chat = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!.AsObject();
        chat["model"]!.GetValue<string>().ShouldBe("chat-model");
        chat["messages"]![0]!["role"]!.GetValue<string>().ShouldBe("user");
        chat.ContainsKey("stream").ShouldBeFalse();
    }

    [Fact]
    public async Task Bridged_stream_translates_chat_sse_to_messages_sse()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () => EventStream(
            """data: {"choices":[{"index":0,"delta":{"content":"Hi"}}]}""" + "\n\n" +
            """data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""" + "\n\n" +
            "data: [DONE]\n\n");
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await PostMessages(client, BridgeModel, stream: true);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        body.ShouldContain("event: message_start");
        body.ShouldContain($"\"model\":\"{BridgeModel}\"");
        body.ShouldContain("event: content_block_delta");
        body.ShouldContain("\"type\":\"text_delta\",\"text\":\"Hi\"");
        body.ShouldContain("event: message_delta");
        body.ShouldContain("event: message_stop");
        body.ShouldNotContain("chatcmpl-1");

        JsonObject request = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!.AsObject();
        request["stream"]!.GetValue<bool>().ShouldBeTrue();
        request["stream_options"]!["include_usage"]!.GetValue<bool>().ShouldBeTrue();
        fixture.Upstream.LastHeaders["accept"].ShouldBe("text/event-stream");
    }

    [Fact]
    public async Task Bridged_streaming_request_with_json_reply_is_replayed()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () => Json(HttpStatusCode.OK,
            """{"choices":[{"index":0,"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""");
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await PostMessages(client, BridgeModel, stream: true);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldContain("event: message_start");
        body.ShouldContain("\"type\":\"text_delta\",\"text\":\"Hello\"");
        body.ShouldContain("event: message_stop");
    }

    [Fact]
    public async Task Count_tokens_is_answered_locally()
    {
        using var fixture = new BridgeAppFixture();
        HttpClient client = fixture.CreateClient();
        int before = fixture.Upstream.RequestCount;

        using var content = new StringContent(
            """{"model":"claude-bridge-sonnet","messages":[{"role":"user","content":"hello"}]}""",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await client.PostAsync("/anthropic/v1/messages/count_tokens", content, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        JsonObject parsed = JsonNode.Parse(body)!.AsObject();
        parsed.Count.ShouldBe(1);
        parsed["input_tokens"]!.GetValue<int>().ShouldBe(9);
        fixture.Upstream.RequestCount.ShouldBe(before);
    }

    [Fact]
    public async Task Count_tokens_rejects_bad_messages_shape()
    {
        using var fixture = new BridgeAppFixture();
        HttpClient client = fixture.CreateClient();
        int before = fixture.Upstream.RequestCount;

        using var content = new StringContent(
            """{"model":"claude-bridge-sonnet"}""",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await client.PostAsync("/anthropic/v1/messages/count_tokens", content, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonObject envelope = JsonNode.Parse(body)!.AsObject();
        envelope["type"]!.GetValue<string>().ShouldBe("error");
        envelope["error"]!["type"]!.GetValue<string>().ShouldBe("invalid_request_error");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("messages: Field required");
        fixture.Upstream.RequestCount.ShouldBe(before);
    }

    [Fact]
    public async Task Non_post_on_bridged_endpoints_is_405()
    {
        using var fixture = new BridgeAppFixture();
        HttpClient client = fixture.CreateClient();

        // A body-less GET cannot resolve a provider, so it falls through to passthrough (NFR-01). A GET
        // that carries a model resolves the bridged route and hits the bridge's endpoint policy.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/anthropic/v1/messages")
        {
            Content = new StringContent(
                """{"model":"claude-bridge-sonnet","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json")
        };
        using HttpResponseMessage response = await client.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.ShouldContain("POST");
        JsonObject envelope = JsonNode.Parse(body)!.AsObject();
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("Method Not Allowed");
    }

    [Fact]
    public async Task Unsupported_bridged_api_is_404()
    {
        using var fixture = new BridgeAppFixture();
        HttpClient client = fixture.CreateClient();

        using var content = new StringContent(
            """{"model":"claude-bridge-sonnet"}""",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await client.PostAsync("/anthropic/v1/files", content, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonObject envelope = JsonNode.Parse(body)!.AsObject();
        envelope["error"]!["type"]!.GetValue<string>().ShouldBe("not_found_error");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("Not found: POST /v1/files");
    }

    [Fact]
    public async Task Request_validation_rejects_typed_server_tool_before_forwarding()
    {
        using var fixture = new BridgeAppFixture();
        HttpClient client = fixture.CreateClient();
        int before = fixture.Upstream.RequestCount;

        using var content = new StringContent(
            """{"model":"claude-bridge-sonnet","max_tokens":100,"tools":[{"type":"web_search_20250305","name":"web_search"}],"messages":[{"role":"user","content":"search"}]}""",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await client.PostAsync("/anthropic/v1/messages", content, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonObject envelope = JsonNode.Parse(body)!.AsObject();
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("tools.0.type: \"web_search_20250305\" server tools are not supported");
        fixture.Upstream.RequestCount.ShouldBe(before);
    }

    [Fact]
    public async Task Upstream_error_is_messages_shaped_and_forwards_retry_headers()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit"}}""");
            response.Headers.Add("Retry-After", "7");
            response.Headers.Add("retry-after-ms", "7000");
            response.Headers.Add("x-ratelimit-remaining-tokens", "0");
            return response;
        };
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await PostMessages(client, BridgeModel, stream: false);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        JsonObject envelope = JsonNode.Parse(body)!.AsObject();
        envelope["type"]!.GetValue<string>().ShouldBe("error");
        envelope["error"]!["type"]!.GetValue<string>().ShouldBe("rate_limit_error");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("Rate limit");
        response.Headers.GetValues("retry-after").Single().ShouldBe("7");
        response.Headers.GetValues("retry-after-ms").Single().ShouldBe("7000");
        response.Headers.Contains("x-ratelimit-remaining-tokens").ShouldBeFalse();
    }

    [Fact]
    public async Task Header_allowlist_excludes_every_caller_header()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () => Json(HttpStatusCode.OK,
            """{"choices":[{"index":0,"message":{"content":"ok"},"finish_reason":"stop"}]}""");
        HttpClient client = fixture.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages");
        request.Headers.Add("cookie", "a=b");
        request.Headers.Add("user-agent", "claude-cli/2.1.207 (external, cli)");
        request.Headers.Add("x-app", "cli");
        request.Headers.Add("x-stainless-lang", "js");
        request.Headers.Add("x-api-key", "caller-key");
        request.Headers.Add("anthropic-version", "2099-01-01");
        request.Headers.Add("anthropic-beta", "a,b,c");
        request.Headers.Add("anthropic-dangerous-direct-browser-access", "true");
        request.Headers.Add("x-claude-code-session-id", "s1");
        request.Content = new StringContent(
            """{"model":"claude-bridge-sonnet","max_tokens":100,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        IReadOnlyDictionary<string, string> headers = fixture.Upstream.LastHeaders;
        foreach (string forbidden in (string[])["cookie", "x-app", "x-stainless-lang", "anthropic-version", "anthropic-beta", "anthropic-dangerous-direct-browser-access", "x-claude-code-session-id"])
        {
            headers.ContainsKey(forbidden).ShouldBeFalse(forbidden);
        }

        headers["accept"].ShouldBe("application/json");
        headers["authorization"].ShouldBe("Bearer chat-key");
    }

    [Fact]
    public async Task Non_bridged_messages_provider_stays_transparent()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () => Json(HttpStatusCode.OK, """{"id":"msg_x","type":"message","role":"assistant","model":"native-model","content":[],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""");
        HttpClient client = fixture.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages");
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "ctx");
        request.Content = new StringContent(
            """{"model":"claude-native-sonnet","max_tokens":100,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.AbsolutePath.ShouldBe("/v1/messages");
        JsonObject forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!.AsObject();
        forwarded["model"]!.GetValue<string>().ShouldBe("native-model");
        forwarded["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("hi");
        fixture.Upstream.LastAnthropicVersion.ShouldBe("2023-06-01");
        fixture.Upstream.LastAnthropicBeta.ShouldBe("ctx");
        body.ShouldContain("native-model");
    }

    [Fact]
    public async Task Bridged_query_string_is_not_forwarded()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () => Json(HttpStatusCode.OK,
            """{"choices":[{"index":0,"message":{"content":"ok"},"finish_reason":"stop"}]}""");
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/anthropic/v1/messages?beta=true",
            new StringContent(
                """{"model":"claude-bridge-sonnet","max_tokens":100,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.AbsolutePath.ShouldBe("/v1/chat/completions");
        fixture.Upstream.LastRequestUri.Query.ShouldBeEmpty();
    }

    [Fact]
    public async Task In_band_error_on_a_2xx_reply_forwards_retry_headers()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () =>
        {
            var response = Json(HttpStatusCode.OK, """{"error":{"code":429,"message":"slow down"}}""");
            response.Headers.Add("Retry-After", "7");
            response.Headers.Add("retry-after-ms", "7000");
            return response;
        };
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await PostMessages(client, BridgeModel, stream: false);
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        JsonObject envelope = JsonNode.Parse(body)!.AsObject();
        envelope["error"]!["type"]!.GetValue<string>().ShouldBe("rate_limit_error");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("slow down");
        response.Headers.GetValues("retry-after").Single().ShouldBe("7");
        response.Headers.GetValues("retry-after-ms").Single().ShouldBe("7000");
    }

    [Fact]
    public async Task Stream_error_frame_redacts_the_configured_provider_key()
    {
        using var fixture = new BridgeAppFixture();
        fixture.Upstream.ResponseFactory = () => EventStream(
            "data: {\"error\":{\"type\":\"authentication_error\",\"message\":\"Incorrect API key provided: chat-key\"}}\n\n");
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await PostMessages(client, BridgeModel, stream: true);
        string body = await response.Content.ReadAsStringAsync(Ct);

        body.ShouldContain("event: error");
        body.ShouldContain("The upstream provider rejected the API key configured for this route");
        body.ShouldContain("***");
        body.ShouldNotContain("chat-key");
    }

    [Fact]
    public async Task Stream_parses_bom_crlf_and_lone_cr()
    {
        using var fixture = new BridgeAppFixture();
        const string role = """{"choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""";
        const string text = """{"choices":[{"index":0,"delta":{"content":"Hi"}}]}""";
        fixture.Upstream.ResponseFactory = () => EventStream(
            "\uFEFF" +
            "data: " + role + "\r\n\r\n" +
            "data: " + text + "\r\r" +
            "data: [DONE]\r\r");
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await PostMessages(client, BridgeModel, stream: true);
        string body = await response.Content.ReadAsStringAsync(Ct);

        body.ShouldContain("event: message_start");
        body.ShouldContain("\"type\":\"text_delta\",\"text\":\"Hi\"");
        body.ShouldContain("event: message_stop");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<HttpResponseMessage> PostMessages(HttpClient client, string model, bool stream)
    {
        string body = $"{{\"model\":\"{model}\",\"max_tokens\":100,\"stream\":{(stream ? "true" : "false")},\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}]}}";
        return await client.PostAsync("/anthropic/v1/messages", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage EventStream(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
        return response;
    }

    private sealed class BridgeAppFixture : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

        private static readonly Dictionary<string, string?> Config = new()
        {
            ["Imposter:Providers:chat-bridged:Dialect"] = "anthropic",
            ["Imposter:Providers:chat-bridged:BaseUrl"] = "https://chat.test",
            ["Imposter:Providers:chat-bridged:Secret"] = "chat-key",
            ["Imposter:Providers:chat-bridged:AuthScheme"] = "Bearer",
            ["Imposter:Providers:chat-bridged:AnthropicUpstreamApi"] = "chat_completions",
            ["Imposter:Providers:chat-bridged:Models:0:From"] = "claude-bridge-*",
            ["Imposter:Providers:chat-bridged:Models:0:To"] = "chat-model",

            ["Imposter:Providers:anthropic-native:Dialect"] = "anthropic",
            ["Imposter:Providers:anthropic-native:BaseUrl"] = "https://native.test",
            ["Imposter:Providers:anthropic-native:Secret"] = "native-key",
            ["Imposter:Providers:anthropic-native:IsDefault"] = "true",
            ["Imposter:Providers:anthropic-native:Models:0:From"] = "claude-native-*",
            ["Imposter:Providers:anthropic-native:Models:0:To"] = "native-model"
        };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddInMemoryCollection(Config);
            });

            builder.ConfigureServices(services =>
                services.AddHttpClient("imposter-upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream));
        }
    }
}
