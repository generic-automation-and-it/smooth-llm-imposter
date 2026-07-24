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
    public async Task Matched_openai_imposter_normalizes_tools_for_strict_upstream()
    {
        HttpClient client = fixture.CreateClient();

        // opencode-go opts into codex_to_openai_sdk normalization. A Codex-shaped catalog (namespace
        // wrapper + unsupported type + dotted name) must be reduced to only valid function tools, and
        // still reach the upstream as 200.
        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json("""
            {"model":"gpt5.4","messages":[{"role":"user","content":"hi"}],
             "tools":[
               {"type":"namespace","name":"mcp__codex_apps__github","tools":[
                 {"type":"function","name":"_search_issues","parameters":{"type":"object"}}
               ]},
               {"type":"web_search","external_web_access":true},
               {"type":"function","name":"multi_tool_use.parallel"},
               {"type":"function","name":"exec_command","parameters":{"type":"object"}}
             ],
             "tool_choice":{"type":"function","name":"multi_tool_use.parallel"}}
            """),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");

        JsonObject forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!.AsObject();
        // Survivors only: flattened _search_issues + exec_command; web_search and the dotted name dropped.
        string[] names = [.. forwarded["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())];
        names.ShouldBe(["_search_issues", "exec_command"]);
        // tool_choice referenced a dropped tool → removed (request-only; upstream falls back to default).
        forwarded.ContainsKey("tool_choice").ShouldBeFalse();
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
        string upstreamBody = "data: {\"delta\":\"hi\"}\n\ndata: [DONE]\n\n";
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(upstreamBody, Encoding.UTF8, "text/event-stream")
        };

        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4","stream":true}"""),
            Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        string body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldBe(upstreamBody);
    }

    [Fact]
    public async Task Openai_responses_request_to_chat_upstream_translates_chat_sse_to_responses_sse()
    {
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

                data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        };

        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/responses",
            Json("""{"model":"gpt5.4","input":"Say hi","stream":true}"""),
            Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        string body = await response.Content.ReadAsStringAsync(Ct);

        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");
        body.ShouldContain("event: response.output_text.delta");
        body.ShouldContain("\"delta\":\"hi\"");
        body.ShouldContain("event: response.completed");
        body.ShouldNotContain("chat.completion.chunk");
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
    public async Task Get_anthropic_models_under_prefix_is_answered_locally_ignoring_query_string()
    {
        // reset to default JSON response (a previous test may have set a streaming factory)
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
        };

        HttpClient client = fixture.CreateClient();
        int upstreamBefore = fixture.Upstream.RequestCount;

        // HLD 005 (Anthropic scope): GET /anthropic/v1/models is synthesized locally from the route catalogue,
        // NOT forwarded — even with a discovery query string. The probe's `?client_version=...` is irrelevant to
        // local aggregation and must not produce an upstream call (NFR-03).
        using HttpResponseMessage response = await client.GetAsync("/anthropic/v1/models?client_version=0.138.0", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        // The Anthropic catalogue's single `to` target is advertised; the upstream is never touched (shared
        // fixture — assert on the request-count delta, not the cross-test LastRequestUri).
        JsonNode root = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        root["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>())
            .ShouldBe(["claude-3-5-haiku-latest"]);
        fixture.Upstream.RequestCount.ShouldBe(upstreamBefore);
    }

    [Fact]
    public async Task Post_to_anthropic_models_still_passes_through_to_default()
    {
        // GET /anthropic/v1/models is answered locally (HLD 005), but a NON-GET on the same path stays
        // transparent passthrough (LADR-03): it must reach the upstream like any other proxied request.
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        };

        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/anthropic/v1/models", content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.anthropic.test/v1/models");
        fixture.Upstream.LastRequestMethod.ShouldBe(HttpMethod.Post);
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

    [Fact]
    public async Task Session_forwarding_stamps_header_and_body_on_opted_in_imposter()
    {
        HttpClient client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = Json("""{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.TryAddWithoutValidation("session_id", "codex-session-1");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastHeaders["x-opencode-session"].ShouldBe("codex-session-1");
        JsonNode forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!;
        forwarded["session_id"]!.GetValue<string>().ShouldBe("codex-session-1");
        forwarded["model"]!.GetValue<string>().ShouldBe("grok-code");
    }

    [Fact]
    public async Task Session_forwarding_is_absent_on_passthrough()
    {
        HttpClient client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = Json("""{"model":"gpt5.5","messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.TryAddWithoutValidation("session_id", "should-not-stamp-body");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.openai.test/v1/chat/completions");
        JsonObject forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!.AsObject();
        forwarded.ContainsKey("session_id").ShouldBeFalse();
        // Caller header may still be relayed verbatim on passthrough; body stamp must not happen.
    }

    [Fact]
    public async Task Session_forwarding_derives_identity_when_caller_sends_none()
    {
        HttpClient client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = Json("""{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-stable");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string stamped = fixture.Upstream.LastHeaders["x-opencode-session"];
        stamped.StartsWith("derived-").ShouldBeTrue();
        JsonNode forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!;
        forwarded["session_id"]!.GetValue<string>().ShouldBe(stamped);
    }
}
