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
    public void OpenAi_chat_upstream_preserves_paired_responses_tool_history()
    {
        var transformer = OpenAi();
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"city\":\"Paris\"}"},
          {"type":"function_call","call_id":"call_2","name":"calc","arguments":"{}"},
          {"type":"function_call_output","call_id":"call_2","output":"2"},
          {"type":"function_call_output","call_id":"call_1","output":"Paris"},
          {"role":"user","content":[{"type":"input_text","text":"continue"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages.Count.ShouldBe(4);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("assistant");
        JsonArray toolCalls = messages[0]!["tool_calls"]!.AsArray();
        toolCalls.Select(t => t!["id"]!.GetValue<string>()).ShouldBe(["call_1", "call_2"]);
        toolCalls[0]!["function"]!["name"]!.GetValue<string>().ShouldBe("lookup");
        toolCalls[0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("""{"city":"Paris"}""");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("tool");
        messages[1]!["tool_call_id"]!.GetValue<string>().ShouldBe("call_1");
        messages[1]!["content"]!.GetValue<string>().ShouldBe("Paris");
        messages[2]!["role"]!.GetValue<string>().ShouldBe("tool");
        messages[2]!["tool_call_id"]!.GetValue<string>().ShouldBe("call_2");
        messages[2]!["content"]!.GetValue<string>().ShouldBe("2");
        messages[3]!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void OpenAi_chat_upstream_removes_orphaned_responses_tool_history()
    {
        var transformer = OpenAi();
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"function_call","call_id":"call_missing","name":"lookup","arguments":"{}"},
          {"type":"function_call_output","call_id":"call_orphan","output":"orphan"},
          {"role":"user","content":[{"type":"input_text","text":"hi"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages.Count.ShouldBe(1);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("hi");
    }

    [Fact]
    public void OpenAi_chat_upstream_does_not_pair_tool_output_across_message_boundary()
    {
        var transformer = OpenAi();
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"},
          {"role":"user","content":[{"type":"input_text","text":"new turn"}]},
          {"type":"function_call_output","call_id":"call_1","output":"late"}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages.Count.ShouldBe(1);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("new turn");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_previous_response_id()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","previous_response_id":"resp_123","input":"continue"}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"));

        ex.StatusCode.ShouldBe(400);
        ex.Message.ShouldContain("previous_response_id");
    }

    [Fact]
    public void OpenAi_responses_upstream_keeps_previous_response_id()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","previous_response_id":"resp_123","input":"continue"}""";

        JsonObject result = JsonNode.Parse(transformer.Transform(body, Decision("gpt5.5", caching: false), "gpt5.4"))!.AsObject();

        result["previous_response_id"]!.GetValue<string>().ShouldBe("resp_123");
        result["input"]!.GetValue<string>().ShouldBe("continue");
    }

    [Fact]
    public void OpenAi_chat_upstream_converts_responses_text_format_to_response_format()
    {
        var transformer = OpenAi();
        string body = """
        {"model":"gpt5.4","input":"Jane, 54","text":{"format":{
          "type":"json_schema",
          "name":"person",
          "strict":true,
          "schema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}
        }}}
        """;

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!.AsObject();

        result.ContainsKey("text").ShouldBeFalse();
        JsonObject responseFormat = result["response_format"]!.AsObject();
        responseFormat["type"]!.GetValue<string>().ShouldBe("json_schema");
        JsonObject jsonSchema = responseFormat["json_schema"]!.AsObject();
        jsonSchema["name"]!.GetValue<string>().ShouldBe("person");
        jsonSchema["strict"]!.GetValue<bool>().ShouldBeTrue();
        jsonSchema["schema"]!["required"]!.AsArray()[0]!.GetValue<string>().ShouldBe("name");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_unsupported_responses_text_format()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":"hi","text":{"format":{"type":"grammar"}}}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"));

        ex.Message.ShouldContain("text.format type 'grammar'");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_json_schema_text_format_missing_name_and_schema()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":"hi","text":{"format":{"type":"json_schema","strict":true}}}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"));

        ex.Message.ShouldContain("json_schema requires name and schema");
    }

    [Theory]
    [InlineData("json_object")]
    [InlineData("text")]
    public void OpenAi_chat_upstream_passes_through_simple_responses_text_format(string formatType)
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":"hi","text":{"format":{"type":"FORMAT"}}}""".Replace("FORMAT", formatType);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!.AsObject();

        result.ContainsKey("text").ShouldBeFalse();
        JsonObject responseFormat = result["response_format"]!.AsObject();
        responseFormat["type"]!.GetValue<string>().ShouldBe(formatType);
        responseFormat.ContainsKey("json_schema").ShouldBeFalse();
    }

    [Fact]
    public void OpenAi_chat_upstream_pairs_independent_tool_runs_separated_by_message()
    {
        var transformer = OpenAi();
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"},
          {"type":"function_call_output","call_id":"call_1","output":"first"},
          {"role":"user","content":[{"type":"input_text","text":"again"}]},
          {"type":"function_call","call_id":"call_2","name":"lookup","arguments":"{}"},
          {"type":"function_call_output","call_id":"call_2","output":"second"}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages.Count.ShouldBe(5);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("assistant");
        messages[0]!["tool_calls"]!.AsArray()[0]!["id"]!.GetValue<string>().ShouldBe("call_1");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("tool");
        messages[1]!["content"]!.GetValue<string>().ShouldBe("first");
        messages[2]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[3]!["role"]!.GetValue<string>().ShouldBe("assistant");
        messages[3]!["tool_calls"]!.AsArray()[0]!["id"]!.GetValue<string>().ShouldBe("call_2");
        messages[4]!["role"]!.GetValue<string>().ShouldBe("tool");
        messages[4]!["content"]!.GetValue<string>().ShouldBe("second");
    }

    [Fact]
    public void OpenAi_chat_upstream_removes_responses_only_reasoning_and_hosted_tool_items()
    {
        var transformer = OpenAi();
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"reasoning","summary":[{"type":"summary_text","text":"thinking"}]},
          {"type":"web_search_call","id":"ws_1","status":"completed"},
          {"role":"user","content":[{"type":"input_text","text":"hi"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages.Count.ShouldBe(1);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("hi");
    }

    [Fact]
    public void OpenAi_chat_upstream_stringifies_structured_function_call_output_array()
    {
        var transformer = OpenAi();
        // Newer Responses payloads can send function_call_output.output as a content-part array rather than a
        // plain string. Chat Completions tool content must be a string, so the array is JSON-stringified
        // (structure preserved as JSON text, not reduced to a text part) — LADR-03 pinned policy.
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"},
          {"type":"function_call_output","call_id":"call_1","output":[{"type":"output_text","text":"Paris"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"))!["messages"]!.AsArray();

        messages.Count.ShouldBe(2);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("assistant");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("tool");
        messages[1]!["tool_call_id"]!.GetValue<string>().ShouldBe("call_1");
        JsonNode toolContent = messages[1]!["content"]!;
        toolContent.GetValueKind().ShouldBe(JsonValueKind.String);
        toolContent.GetValue<string>().ShouldBe("""[{"type":"output_text","text":"Paris"}]""");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_hosted_tool_item_without_call_suffix()
    {
        var transformer = OpenAi();
        // mcp_list_tools / mcp_approval_request are hosted-tool Items whose type does not end in
        // _call/_call_output, so they fall outside IsHostedToolItem's remove set and reject by policy
        // (LADR-03 fail-fast) rather than being silently dropped or converted to an empty user message.
        string body = """{"model":"gpt5.4","input":[{"type":"mcp_list_tools","id":"mcp_1"}]}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"));

        ex.Message.ShouldContain("mcp_list_tools");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_unknown_responses_input_item_type()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":[{"type":"mystery_item","role":"user","content":"x"}]}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4"));

        ex.Message.ShouldContain("mystery_item");
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
