using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Normalization;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class RequestTransformerTests
{
    private static OpenAiRequestTransformer OpenAi() => new([new CodexToOpenAiSdkNormalizer()]);

    private static RouteDecision Decision(string targetModel, bool caching) =>
        new(
            new ProviderRoute("p", ApiDialect.OpenAi, new Uri("https://p.example"), null, false, null, []),
            targetModel,
            caching,
            IsImposter: true);

    private static RouteDecision ChatDecision(string targetModel, bool caching) =>
        new(
            new ProviderRoute("p", ApiDialect.OpenAi, new Uri("https://p.example"), null, false, null, [], OpenAiUpstreamApi.ChatCompletions),
            targetModel,
            caching,
            IsImposter: true);

    private static RouteDecision NormalizingChatDecision(bool isImposter) =>
        new(
            new ProviderRoute(
                "p", ApiDialect.OpenAi, new Uri("https://p.example"), null, false, null, [],
                OpenAiUpstreamApi.ChatCompletions, null, RequestNormalization.CodexToOpenAiSdk),
            "kimi",
            CachingEnabled: false,
            IsImposter: isImposter);

    [Fact]
    public void OpenAi_rewrites_model_and_sets_cache_key_when_caching_enabled()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("grok-code", caching: true), "gpt5.4"))!;

        result["model"]!.GetValue<string>().ShouldBe("grok-code");
        result["prompt_cache_key"]!.GetValue<string>().ShouldBe("gpt5.4");
    }

    [Fact]
    public void OpenAi_does_not_set_cache_key_when_caching_disabled()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.5"}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("gpt5.5", caching: false), "gpt5.5"))!;

        result["model"]!.GetValue<string>().ShouldBe("gpt5.5");
        result.AsObject().ContainsKey("prompt_cache_key").ShouldBeFalse();
    }

    [Fact]
    public void OpenAi_normalization_drops_invalid_tools_then_nests_survivors_for_chat_upstream()
    {
        var transformer = OpenAi();
        // Flat Responses tools incl. a namespace wrapper + an unsupported type. Normalization runs before
        // the Responses→Chat conversion, so survivors come back in nested Chat shape.
        string body = """
        {"model":"gpt5.4","messages":[{"role":"user","content":"hi"}],
         "tools":[
           {"type":"namespace","name":"ns","tools":[{"type":"function","name":"_search_issues"}]},
           {"type":"web_search"},
           {"type":"function","name":"exec_command"}
         ]}
        """;

        JsonObject result = JsonNode.Parse(transformer.Transform(body, NormalizingChatDecision(isImposter: true), "gpt5.4"))!.AsObject();

        string[] names = [.. result["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())];
        names.ShouldBe(["_search_issues", "exec_command"]);
        result["tools"]!.AsArray().All(t => t!["type"]!.GetValue<string>() == "function").ShouldBeTrue();
    }

    [Fact]
    public void OpenAi_normalization_is_noop_when_provider_opts_out()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","tools":[{"type":"web_search"}]}""";

        // Default ChatDecision provider has RequestNormalization = None ⇒ tools forwarded unchanged.
        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!.AsObject();

        result["tools"]!.AsArray()[0]!["type"]!.GetValue<string>().ShouldBe("web_search");
    }

    [Fact]
    public void OpenAi_normalization_is_skipped_on_passthrough_even_when_provider_opts_in()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","tools":[{"type":"web_search"}]}""";

        // IsImposter:false ⇒ normalization must not run (scope: matched imposter routes only).
        JsonObject result = JsonNode.Parse(transformer.Transform(body, NormalizingChatDecision(isImposter: false), "gpt5.4"))!.AsObject();

        result["tools"]!.AsArray()[0]!["type"]!.GetValue<string>().ShouldBe("web_search");
    }

    [Fact]
    public void OpenAi_chat_upstream_folds_developer_role_into_system()
    {
        var transformer = OpenAi();
        // Codex /responses sends a developer-role input item; Moonshot/kimi's chat template rejects
        // "developer" ("tokenization failed"), so the chat conversion must fold it to "system".
        string body = """
        {"model":"gpt5.4","input":[
          {"role":"developer","content":[{"type":"input_text","text":"be brief"}]},
          {"role":"user","content":[{"type":"input_text","text":"hi"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("be brief");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void OpenAi_chat_upstream_folds_developer_role_in_existing_messages()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","messages":[{"role":"developer","content":"sys"},{"role":"user","content":"hi"}]}""";

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("sys");
    }

    [Fact]
    public void OpenAi_chat_upstream_converts_responses_input_to_messages()
    {
        var transformer = OpenAi();
        string body = """
        {
          "model":"gpt5.4",
          "instructions":"be direct",
          "input":[{"role":"user","content":[{"type":"input_text","text":"hi"}]}],
          "max_output_tokens":321
        }
        """;

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi-k2.7", caching: true), "gpt5.4"))!.AsObject();

        result["model"]!.GetValue<string>().ShouldBe("kimi-k2.7");
        result["max_tokens"]!.GetValue<int>().ShouldBe(321);
        result.ContainsKey("input").ShouldBeFalse();
        result.ContainsKey("prompt_cache_key").ShouldBeFalse();

        JsonArray messages = result["messages"]!.AsArray();
        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("be direct");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[1]!["content"]!.GetValue<string>().ShouldBe("hi");
    }

    [Fact]
    public void Anthropic_string_system_becomes_block_with_ephemeral_cache_control()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """{"model":"claude-haiku-x","system":"you are helpful","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("claude-3-5-haiku-latest", caching: true), "claude-haiku-x"))!;

        result["model"]!.GetValue<string>().ShouldBe("claude-3-5-haiku-latest");
        JsonArray system = result["system"]!.AsArray();
        system.Count.ShouldBe(1);
        system[0]!["text"]!.GetValue<string>().ShouldBe("you are helpful");
        system[0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public void Anthropic_marks_last_block_of_last_message()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """
        {"model":"claude-haiku-x","messages":[
          {"role":"user","content":[{"type":"text","text":"first"},{"type":"text","text":"last"}]}
        ]}
        """;

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("target", caching: true), "claude-haiku-x"))!;

        JsonArray content = result["messages"]!.AsArray()[0]!["content"]!.AsArray();
        content[0]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
        content[1]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public void Anthropic_without_caching_only_rewrites_model()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """{"model":"claude-x","system":"sys","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("target", caching: false), "claude-x"))!;

        result["model"]!.GetValue<string>().ShouldBe("target");
        result["system"]!.GetValueKind().ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public void Invalid_json_throws_routing_exception()
    {
        var transformer = OpenAi();
        Should.Throw<RoutingException>(() => transformer.Transform("not json", Decision("x", false), "x"));
    }
}
