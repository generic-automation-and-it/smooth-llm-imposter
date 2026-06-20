using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Host.IntegrationTest;

public sealed class RoutingIntegrationTests(ImposterAppFixture fixture) : IClassFixture<ImposterAppFixture>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Matched_openai_model_is_rewritten_and_routed_to_imposter_with_key_and_cache()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}"""),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");
        // opencode-go authenticates with x-api-key (AuthScheme: ApiKey), not Bearer, despite the openai dialect.
        fixture.Upstream.LastApiKey.ShouldBe("opencode-key");
        fixture.Upstream.LastAuthorization.ShouldBeNull();

        JsonNode forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!;
        forwarded["model"]!.GetValue<string>().ShouldBe("grok-code");
        forwarded.AsObject().ContainsKey("prompt_cache_key").ShouldBeFalse();
    }

    [Fact]
    public async Task Unmatched_openai_model_passes_through_to_default_unchanged()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.5"}"""),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.openai.test/v1/chat/completions");
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer openai-key");

        JsonNode forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!;
        forwarded["model"]!.GetValue<string>().ShouldBe("gpt5.5");
        forwarded.AsObject().ContainsKey("prompt_cache_key").ShouldBeFalse();
    }

    [Fact]
    public async Task Anthropic_wildcard_match_rewrites_model_and_injects_cache_control()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/messages",
            Json("""{"model":"claude-haiku-20241022","system":"you are helpful","messages":[{"role":"user","content":"hi"}]}"""),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.anthropic.test/v1/messages");
        fixture.Upstream.LastApiKey.ShouldBe("anthropic-key");
        fixture.Upstream.LastAnthropicVersion.ShouldBe("2023-06-01");

        JsonNode forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!;
        forwarded["model"]!.GetValue<string>().ShouldBe("claude-3-5-haiku-latest");
        forwarded["system"]!.AsArray()[0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public async Task Streaming_response_is_passed_through_as_event_stream()
    {
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"delta\":\"hi\"}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        };

        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4","stream":true}"""),
            Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        string body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("data: [DONE]");
    }

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        HttpClient client = fixture.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Openai_prefix_matches_imposter_and_strips_prefix_from_upstream_path()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            Json("""{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}"""),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Prefix stripped: upstream path is /v1/chat/completions, not /openai/v1/chat/completions.
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");

        JsonNode forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!;
        forwarded["model"]!.GetValue<string>().ShouldBe("grok-code");
    }

    [Fact]
    public async Task Openai_responses_request_to_chat_upstream_rewrites_path_and_body()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/responses",
            Json("""
            {
              "model":"gpt5.4",
              "instructions":"be direct",
              "input":[{"role":"user","content":[{"type":"input_text","text":"hi"}]}],
              "stream":true,
              "max_output_tokens":123
            }
            """),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");

        JsonObject forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!.AsObject();
        forwarded["model"]!.GetValue<string>().ShouldBe("grok-code");
        forwarded["stream"]!.GetValue<bool>().ShouldBeTrue();
        forwarded["max_tokens"]!.GetValue<int>().ShouldBe(123);
        forwarded.ContainsKey("input").ShouldBeFalse();
        forwarded.ContainsKey("prompt_cache_key").ShouldBeFalse();

        JsonArray messages = forwarded["messages"]!.AsArray();
        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("be direct");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[1]!["content"]!.GetValue<string>().ShouldBe("hi");
    }

    [Fact]
    public async Task Anthropic_prefix_routes_to_anthropic_dialect_default()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/anthropic/v1/messages",
            Json("""{"model":"claude-opus-4","messages":[{"role":"user","content":"hi"}]}"""),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.anthropic.test/v1/messages");
        fixture.Upstream.LastApiKey.ShouldBe("anthropic-key");
    }

    [Fact]
    public async Task Get_anthropic_models_under_prefix_passes_through_to_default_without_body()
    {
        // reset to default JSON response (a previous test may have set a streaming factory)
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
        };

        HttpClient client = fixture.CreateClient();

        // Aggregation is scoped to OpenAI only (HLD 005, LADR-03); Anthropic discovery stays transparent
        // passthrough — no model to match → routed to the Anthropic default, path + query forwarded verbatim.
        using HttpResponseMessage response = await client.GetAsync("/anthropic/v1/models?client_version=0.138.0", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.anthropic.test/v1/models?client_version=0.138.0");
        fixture.Upstream.LastRequestMethod.ShouldBe(HttpMethod.Get);
        fixture.Upstream.LastRequestBody.ShouldBeNull();
        fixture.Upstream.LastApiKey.ShouldBe("anthropic-key");
    }

    [Fact]
    public async Task Missing_model_returns_dialect_shaped_error()
    {
        // reset to default JSON response (previous test may have set a streaming factory)
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        };

        HttpClient client = fixture.CreateClient();
        using HttpResponseMessage response = await client.PostAsync("/v1/messages", Json("""{"messages":[]}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonNode error = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        error["type"]!.GetValue<string>().ShouldBe("error"); // anthropic-shaped
    }
}
