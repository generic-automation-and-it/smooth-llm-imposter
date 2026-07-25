using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

/// <summary>
/// L0 unit tests for <see cref="WhoMessageResponder"/> (HLD 010). Exercises the trigger predicate,
/// envelope shape, auth-scheme propagation, streaming exclusion, and the no-secret-leakage NFR.
/// Also covers the new <c>--who?</c> and <c>--newsession</c> switches and the session translation
/// dictionary integration.
/// </summary>
public class WhoMessageResponderTests
{
    private const string InboundModel = "gpt-5.4";
    private const string TargetModel = "grok-code-5";
    private const string SecretValue = "sk-super-secret-key-12345";

    private static readonly Uri BaseUrl = new("https://provider.example");

    private static ProviderRoute OpenAiRoute(string? secret = SecretValue, CredentialAuthScheme? authScheme = null, bool isDefault = false) =>
        new("opencode", ApiDialect.OpenAi, BaseUrl, secret, isDefault, AnthropicVersion: null, Models: [], AuthScheme: authScheme);

    private static ProviderRoute AnthropicRoute(string? secret = SecretValue, CredentialAuthScheme? authScheme = null, bool isDefault = false) =>
        new("opencode-anthropic", ApiDialect.Anthropic, BaseUrl, secret, isDefault, AnthropicVersion: null, Models: [], AuthScheme: authScheme);

    private static RoutePlan ImposterPlan(ProviderRoute route, string inbound = InboundModel, string target = TargetModel, SessionIdentity? sessionIdentity = null) =>
        new(new RouteDecision(route, target, CachingEnabled: false, IsImposter: true), inbound, TransformedBody: "{}", sessionIdentity ?? SessionIdentity.None);

    private static RoutePlan PassthroughPlan(ProviderRoute route, string inbound = InboundModel, RouteCredentialOverride? credentialOverride = null) =>
        new(new RouteDecision(route, inbound, CachingEnabled: false, IsImposter: false), inbound, TransformedBody: "{}", SessionIdentity.None, credentialOverride);

    private static string OpenAiBody(string lastUserContent, bool stream = false) =>
        $$"""{"model":"gpt-5.4","messages":[{"role":"user","content":{{System.Text.Json.JsonSerializer.Serialize(lastUserContent)}}}]{{(stream ? ",\"stream\":true" : "")}}}""";

    private static string OpenAiBodyWithParts(string[] textParts, bool includeNonText = false)
    {
        var parts = new JsonArray();
        foreach (string text in textParts)
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        if (includeNonText)
        {
            parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "https://example/x" } });
        }

        var body = new JsonObject
        {
            ["model"] = "gpt-5.4",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = parts },
            },
        };
        return body.ToJsonString();
    }

    private static WhoMessageResponder CreateResponder(ISessionTranslationDictionary? dictionary = null) =>
        new(dictionary ?? new InMemorySessionTranslationDictionary(), NullLogger<WhoMessageResponder>.Instance);

    [Fact]
    public void OpenAi_imposter_match_returns_chat_completion_envelope_with_route_description()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json);

        matched.ShouldBeTrue();
        JsonObject root = JsonNode.Parse(json!)!.AsObject();
        root["object"]!.GetValue<string>().ShouldBe("chat.completion");
        root["model"]!.GetValue<string>().ShouldBe(InboundModel);
        JsonNode choice = root["choices"]!.AsArray().Single()!;
        choice["finish_reason"]!.GetValue<string>().ShouldBe("stop");
        string content = choice["message"]!["content"]!.GetValue<string>();
        // OpenAI dialect default scheme is Bearer (UpstreamAuthResolver.DefaultSchemeFor) when the
        // provider's AuthScheme is null. If this assertion ever fails on auth-scheme, check the
        // dialect default, not the responder — the responder just echoes DescribeAuth's return.
        content.ShouldBe($"Imposter: {InboundModel} → {TargetModel} (auth: Bearer, session: null)");
        root["usage"]!["total_tokens"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void Anthropic_imposter_match_returns_message_envelope_with_text_content_block()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(AnthropicRoute(authScheme: CredentialAuthScheme.ApiKey));

        bool matched = responder.TryBuildResponse(ApiDialect.Anthropic, OpenAiBody("--who?"), plan, out string? json);

        matched.ShouldBeTrue();
        JsonObject root = JsonNode.Parse(json!)!.AsObject();
        root["type"]!.GetValue<string>().ShouldBe("message");
        root["role"]!.GetValue<string>().ShouldBe("assistant");
        root["stop_reason"]!.GetValue<string>().ShouldBe("end_turn");
        JsonNode textBlock = root["content"]!.AsArray().Single()!;
        textBlock["type"]!.GetValue<string>().ShouldBe("text");
        textBlock["text"]!.GetValue<string>().ShouldBe($"Imposter: {InboundModel} → {TargetModel} (auth: ApiKey, session: null)");
        root["usage"]!["input_tokens"]!.GetValue<int>().ShouldBe(0);
        root["usage"]!["output_tokens"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void Passthrough_route_uses_passthrough_prefix_and_reports_caller_passthrough_auth_when_no_credential()
    {
        var responder = CreateResponder();
        // No configured secret + not an imposter route → DescribeAuth reports "caller-passthrough".
        RoutePlan plan = PassthroughPlan(OpenAiRoute(secret: null, isDefault: true));

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json);

        matched.ShouldBeTrue();
        string content = JsonNode.Parse(json!)!["choices"]!.AsArray().Single()!["message"]!["content"]!.GetValue<string>();
        content.ShouldBe($"Passthrough: {InboundModel} (auth: caller-passthrough, session: null)");
    }

    [Fact]
    public void Passthrough_route_with_active_stored_credential_reports_bearer_scheme()
    {
        var responder = CreateResponder();
        var credential = new RouteCredentialOverride("sk-stored", CredentialAuthScheme.Bearer, BaseUrlOverride: null, AnthropicVersion: null);
        RoutePlan plan = PassthroughPlan(OpenAiRoute(secret: null, isDefault: true), credentialOverride: credential);

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json);

        matched.ShouldBeTrue();
        string content = JsonNode.Parse(json!)!["choices"]!.AsArray().Single()!["message"]!["content"]!.GetValue<string>();
        // Passthrough + stored Bearer credential → forwarder writes Bearer; the reply reports the same.
        content.ShouldBe($"Passthrough: {InboundModel} (auth: Bearer, session: null)");
    }

    [Fact]
    public void Passthrough_route_with_active_ApiKey_credential_reports_apikey_scheme()
    {
        // Closes the (AuthScheme=ApiKey, IsImposter=false, HasOverride=true) permutation of
        // the NFR-03 "every (AuthScheme, HasSecret, Override) tuple" claim.
        var responder = CreateResponder();
        var credential = new RouteCredentialOverride("sk-stored", CredentialAuthScheme.ApiKey, BaseUrlOverride: null, AnthropicVersion: null);
        RoutePlan plan = PassthroughPlan(OpenAiRoute(secret: null, isDefault: true), credentialOverride: credential);

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json);

        matched.ShouldBeTrue();
        string content = JsonNode.Parse(json!)!["choices"]!.AsArray().Single()!["message"]!["content"]!.GetValue<string>();
        content.ShouldBe($"Passthrough: {InboundModel} (auth: ApiKey, session: null)");
    }

    [Fact]
    public void Streaming_request_is_forwarded_not_intercepted()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?", stream: true), plan, out string? json);

        matched.ShouldBeFalse();
        json.ShouldBeNull();
    }

    [Fact]
    public void Non_trigger_content_does_not_fire()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("hello world"), plan, out _).ShouldBeFalse();
        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("Who?"), plan, out _).ShouldBeFalse("case-sensitive match");
        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("WHO?"), plan, out _).ShouldBeFalse("case-sensitive match");
        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("who"), plan, out _).ShouldBeFalse("missing question mark");
        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("who?"), plan, out _).ShouldBeFalse("bare 'who?' is no longer a trigger; use '--who?'");
    }

    [Fact]
    public void Trimmed_whitespace_still_matches()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("  --who?  "), plan, out string? json).ShouldBeTrue();
        json.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Trigger_matches_last_user_message_only()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        // Last user message carries the trigger, earlier history carries other content → match.
        string body = """
            {"model":"gpt-5.4","messages":[
                {"role":"user","content":"earlier question"},
                {"role":"assistant","content":"earlier reply"},
                {"role":"user","content":"--who?"}
            ]}
            """;
        responder.TryBuildResponse(ApiDialect.OpenAi, body, plan, out _).ShouldBeTrue();

        // Trigger appears in history but the last user message is something else → no match.
        string notLast = """
            {"model":"gpt-5.4","messages":[
                {"role":"user","content":"--who?"},
                {"role":"assistant","content":"some reply"},
                {"role":"user","content":"now do something else"}
            ]}
            """;
        responder.TryBuildResponse(ApiDialect.OpenAi, notLast, plan, out _).ShouldBeFalse();
    }

    [Fact]
    public void Concatenated_text_parts_match_when_all_parts_are_text()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        // Split across two text parts; concatenated value equals "--who?" after trim.
        string body = OpenAiBodyWithParts(["--wh", "o?"]);

        responder.TryBuildResponse(ApiDialect.OpenAi, body, plan, out string? json).ShouldBeTrue();
        json.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Non_text_content_part_in_last_user_message_disables_trigger()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        string body = OpenAiBodyWithParts(["--who?"], includeNonText: true);

        responder.TryBuildResponse(ApiDialect.OpenAi, body, plan, out string? json).ShouldBeFalse();
        json.ShouldBeNull();
    }

    [Fact]
    public void Missing_messages_array_does_not_match()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        string body = """{"model":"gpt-5.4"}""";

        responder.TryBuildResponse(ApiDialect.OpenAi, body, plan, out string? json).ShouldBeFalse();
        json.ShouldBeNull();
    }

    [Fact]
    public void Empty_or_malformed_body_does_not_throw()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        responder.TryBuildResponse(ApiDialect.OpenAi, string.Empty, plan, out string? emptyJson).ShouldBeFalse();
        emptyJson.ShouldBeNull();

        responder.TryBuildResponse(ApiDialect.OpenAi, "not json", plan, out string? badJson).ShouldBeFalse();
        badJson.ShouldBeNull();
    }

    [Fact]
    public void Response_never_contains_the_configured_secret()
    {
        var responder = CreateResponder();
        // Bearer scheme explicitly set so DescribeAuth reports "Bearer" (not the secret value).
        RoutePlan plan = ImposterPlan(OpenAiRoute(secret: SecretValue, authScheme: CredentialAuthScheme.Bearer));

        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json).ShouldBeTrue();

        json.ShouldNotBeNull();
        json.ShouldNotContain(SecretValue);
        // Also check a fragment that a substring-match test would catch.
        json.ShouldNotContain("super-secret");
    }

    [Fact]
    public void Imposter_route_with_no_secret_reports_auth_none()
    {
        var responder = CreateResponder();
        // Imposter route with no configured secret → DescribeAuth returns "none".
        RoutePlan plan = ImposterPlan(OpenAiRoute(secret: null));

        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json).ShouldBeTrue();

        string content = JsonNode.Parse(json!)!["choices"]!.AsArray().Single()!["message"]!["content"]!.GetValue<string>();
        content.ShouldBe($"Imposter: {InboundModel} → {TargetModel} (auth: none, session: null)");
    }

    [Fact]
    public void Synthetic_id_carries_who_prefix_for_greppability()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? openAiJson).ShouldBeTrue();
        responder.TryBuildResponse(ApiDialect.Anthropic, OpenAiBody("--who?"), ImposterPlan(AnthropicRoute()), out string? anthropicJson).ShouldBeTrue();

        JsonNode.Parse(openAiJson!)!["id"]!.GetValue<string>().ShouldStartWith("chatcmpl-who-");
        JsonNode.Parse(anthropicJson!)!["id"]!.GetValue<string>().ShouldStartWith("msg_who_");
    }

    [Fact]
    public void User_message_with_no_content_field_does_not_match()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        string body = """{"model":"gpt-5.4","messages":[{"role":"user"}]}""";

        responder.TryBuildResponse(ApiDialect.OpenAi, body, plan, out string? json).ShouldBeFalse();
        json.ShouldBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // --who? switch tests
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Who_switch_matches_and_includes_session_field()
    {
        var responder = CreateResponder();
        var sessionId = new SessionIdentity("caller-session-abc", SessionIdentitySource.Captured);
        RoutePlan plan = ImposterPlan(OpenAiRoute(), sessionIdentity: sessionId);

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json);

        matched.ShouldBeTrue();
        string content = JsonNode.Parse(json!)!["choices"]!.AsArray().Single()!["message"]!["content"]!.GetValue<string>();
        content.ShouldBe($"Imposter: {InboundModel} → {TargetModel} (auth: Bearer, session: caller-session-abc)");
    }

    [Fact]
    public void Who_switch_without_session_includes_session_null()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute());

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--who?"), plan, out string? json);

        matched.ShouldBeTrue();
        string content = JsonNode.Parse(json!)!["choices"]!.AsArray().Single()!["message"]!["content"]!.GetValue<string>();
        content.ShouldBe($"Imposter: {InboundModel} → {TargetModel} (auth: Bearer, session: null)");
    }


    // ──────────────────────────────────────────────────────────────────────────────
    // --newsession switch tests
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewSession_switch_mints_synthetic_id_and_stores_mapping()
    {
        var dictionary = new InMemorySessionTranslationDictionary();
        var responder = CreateResponder(dictionary);
        var sessionId = new SessionIdentity("caller-session-xyz", SessionIdentitySource.Captured);
        RoutePlan plan = ImposterPlan(OpenAiRoute(), sessionIdentity: sessionId);

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--newsession"), plan, out string? json);

        matched.ShouldBeTrue();
        string content = JsonNode.Parse(json!)!["choices"]!.AsArray().Single()!["message"]!["content"]!.GetValue<string>();
        content.ShouldStartWith("Session: caller-session-xyz → ");

        // Verify the mapping was stored in the dictionary
        dictionary.TryTranslate("caller-session-xyz", out string? syntheticId).ShouldBeTrue();
        syntheticId.ShouldNotBeNullOrEmpty();
        syntheticId!.Length.ShouldBe(32); // Guid.NewGuid().ToString("N") is 32 chars
        content.ShouldEndWith(syntheticId);
    }

    [Fact]
    public void NewSession_switch_without_caller_session_returns_false()
    {
        var responder = CreateResponder();
        RoutePlan plan = ImposterPlan(OpenAiRoute()); // No session identity

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--newsession"), plan, out string? json);

        matched.ShouldBeFalse();
        json.ShouldBeNull();
    }

    [Fact]
    public void NewSession_switch_does_not_overwrite_existing_mapping()
    {
        var dictionary = new InMemorySessionTranslationDictionary();
        var responder = CreateResponder(dictionary);
        var sessionId = new SessionIdentity("caller-session-abc", SessionIdentitySource.Captured);
        RoutePlan plan = ImposterPlan(OpenAiRoute(), sessionIdentity: sessionId);

        // First call mints and stores
        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--newsession"), plan, out _).ShouldBeTrue();
        dictionary.TryTranslate("caller-session-abc", out string? firstSynthetic).ShouldBeTrue();

        // Second call should not overwrite
        responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--newsession"), plan, out _).ShouldBeTrue();
        dictionary.TryTranslate("caller-session-abc", out string? secondSynthetic).ShouldBeTrue();

        // Both should be the same synthetic id
        firstSynthetic.ShouldBe(secondSynthetic);
    }

    [Fact]
    public void NewSession_switch_streaming_does_not_match()
    {
        var responder = CreateResponder();
        var sessionId = new SessionIdentity("caller-session-abc", SessionIdentitySource.Captured);
        RoutePlan plan = ImposterPlan(OpenAiRoute(), sessionIdentity: sessionId);

        bool matched = responder.TryBuildResponse(ApiDialect.OpenAi, OpenAiBody("--newsession", stream: true), plan, out string? json);

        matched.ShouldBeFalse();
        json.ShouldBeNull();
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Session translation dictionary tests
// ──────────────────────────────────────────────────────────────────────────────

public class SessionTranslationDictionaryTests
{
    [Fact]
    public void TryAdd_stores_mapping_and_returns_true()
    {
        var dictionary = new InMemorySessionTranslationDictionary();

        dictionary.TryAdd("caller-1", out string? synthetic).ShouldBeTrue();
        synthetic.ShouldNotBeNullOrEmpty();
        synthetic!.Length.ShouldBe(32);

        dictionary.TryTranslate("caller-1", out string? retrieved).ShouldBeTrue();
        retrieved.ShouldBe(synthetic);
    }

    [Fact]
    public void TryAdd_returns_false_when_key_already_exists()
    {
        var dictionary = new InMemorySessionTranslationDictionary();

        dictionary.TryAdd("caller-1", out string? first).ShouldBeTrue();
        dictionary.TryAdd("caller-1", out string? second).ShouldBeFalse();

        // Both should return the same synthetic id
        first.ShouldBe(second);
    }

    [Fact]
    public void TryTranslate_returns_false_for_unknown_key()
    {
        var dictionary = new InMemorySessionTranslationDictionary();

        dictionary.TryTranslate("unknown-caller", out string? synthetic).ShouldBeFalse();
        synthetic.ShouldBeNull();
    }

    [Fact]
    public void Multiple_callers_get_distinct_synthetic_ids()
    {
        var dictionary = new InMemorySessionTranslationDictionary();

        dictionary.TryAdd("caller-1", out string? synthetic1).ShouldBeTrue();
        dictionary.TryAdd("caller-2", out string? synthetic2).ShouldBeTrue();

        synthetic1.ShouldNotBe(synthetic2);
    }
}
