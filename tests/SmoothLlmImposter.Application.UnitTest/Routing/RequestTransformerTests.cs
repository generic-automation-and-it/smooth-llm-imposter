using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Normalization;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class RequestTransformerTests
{
    private static OpenAiRequestTransformer OpenAi() => new([new CodexToOpenAiSdkNormalizer()]);

    private static readonly SessionIdentity NoSession = SessionIdentity.None;

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

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("grok-code", caching: true), "gpt5.4", NoSession))!;

        result["model"]!.GetValue<string>().ShouldBe("grok-code");
        result["prompt_cache_key"]!.GetValue<string>().ShouldBe("gpt5.4");
    }

    [Fact]
    public void OpenAi_does_not_set_cache_key_when_caching_disabled()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.5"}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("gpt5.5", caching: false), "gpt5.5", NoSession))!;

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

        JsonObject result = JsonNode.Parse(transformer.Transform(body, NormalizingChatDecision(isImposter: true), "gpt5.4", NoSession))!.AsObject();

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
        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!.AsObject();

        result["tools"]!.AsArray()[0]!["type"]!.GetValue<string>().ShouldBe("web_search");
    }

    [Fact]
    public void OpenAi_normalization_is_skipped_on_passthrough_even_when_provider_opts_in()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","tools":[{"type":"web_search"}]}""";

        // IsImposter:false ⇒ normalization must not run (scope: matched imposter routes only).
        JsonObject result = JsonNode.Parse(transformer.Transform(body, NormalizingChatDecision(isImposter: false), "gpt5.4", NoSession))!.AsObject();

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

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("be brief");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void OpenAi_chat_upstream_folds_developer_role_in_existing_messages()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","messages":[{"role":"developer","content":"sys"},{"role":"user","content":"hi"}]}""";

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

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

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi-k2.7", caching: true), "gpt5.4", NoSession))!.AsObject();

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

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

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

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

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

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages.Count.ShouldBe(1);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("new turn");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_previous_response_id()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","previous_response_id":"resp_123","input":"continue"}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession));

        ex.StatusCode.ShouldBe(400);
        ex.Message.ShouldContain("previous_response_id");
    }

    [Fact]
    public void OpenAi_responses_upstream_keeps_previous_response_id()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","previous_response_id":"resp_123","input":"continue"}""";

        JsonObject result = JsonNode.Parse(transformer.Transform(body, Decision("gpt5.5", caching: false), "gpt5.4", NoSession))!.AsObject();

        result["previous_response_id"]!.GetValue<string>().ShouldBe("resp_123");
        result["input"]!.GetValue<string>().ShouldBe("continue");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_conversation()
    {
        var transformer = OpenAi();
        // conversation is the Conversations API state pointer; a stateless Chat upstream cannot resolve it,
        // so the downgrade rejects it like previous_response_id rather than silently dropping it (LADR-03).
        string body = """{"model":"gpt5.4","conversation":"conv_123","input":"continue"}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession));

        ex.StatusCode.ShouldBe(400);
        ex.Message.ShouldContain("conversation");
    }

    [Fact]
    public void OpenAi_chat_upstream_allows_explicit_null_conversation()
    {
        var transformer = OpenAi();
        // An explicit "conversation": null is not a state pointer — the present-and-non-null guard must
        // not falsely reject it.
        string body = """{"model":"gpt5.4","conversation":null,"input":"hi"}""";

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!.AsObject();

        result["model"]!.GetValue<string>().ShouldBe("kimi");
        result.ContainsKey("conversation").ShouldBeFalse();
    }

    [Fact]
    public void OpenAi_responses_upstream_keeps_conversation_and_reasoning()
    {
        var transformer = OpenAi();
        // The real /responses (no-downgrade) path stays byte-transparent for both new fields.
        string body = """{"model":"gpt5.4","conversation":"conv_123","reasoning":{"effort":"high"},"input":"continue"}""";

        JsonObject result = JsonNode.Parse(transformer.Transform(body, Decision("gpt5.5", caching: false), "gpt5.4", NoSession))!.AsObject();

        result["conversation"]!.GetValue<string>().ShouldBe("conv_123");
        result["reasoning"]!["effort"]!.GetValue<string>().ShouldBe("high");
        result.ContainsKey("reasoning_effort").ShouldBeFalse();
        result["input"]!.GetValue<string>().ShouldBe("continue");
    }

    [Fact]
    public void OpenAi_chat_upstream_converts_reasoning_effort_to_chat_reasoning_effort()
    {
        var transformer = OpenAi();
        // Responses reasoning.effort maps to Chat top-level reasoning_effort; the Responses reasoning
        // object (summary/etc.) never leaks into the Chat body (LADR-03 convert).
        string body = """{"model":"gpt5.4","input":"hi","reasoning":{"effort":"high","summary":"auto"}}""";

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!.AsObject();

        result["reasoning_effort"]!.GetValue<string>().ShouldBe("high");
        result.ContainsKey("reasoning").ShouldBeFalse();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("ultra")]
    public void OpenAi_chat_upstream_drops_incompatible_reasoning_effort(string effort)
    {
        var transformer = OpenAi();
        // "none" disables tool calling on GPT-5.4+ Chat Completions and this path is tool-heavy (#19);
        // unknown values are not valid Chat reasoning_effort. Both are dropped, never forwarded (LADR-03).
        string body = """{"model":"gpt5.4","input":"hi","reasoning":{"effort":"EFFORT"}}""".Replace("EFFORT", effort);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!.AsObject();

        result.ContainsKey("reasoning_effort").ShouldBeFalse();
        result.ContainsKey("reasoning").ShouldBeFalse();
    }

    [Fact]
    public void OpenAi_chat_upstream_passes_through_chat_compatible_generation_knobs()
    {
        var transformer = OpenAi();
        // stop/metadata/logit_bias/logprobs/top_logprobs are valid Chat Completions knobs that share their
        // shape with Responses; they survive the downgrade rather than being silently dropped (LADR-03).
        string body = """
        {"model":"gpt5.4","input":"hi",
         "stop":["STOP"],
         "metadata":{"k":"v"},
         "logit_bias":{"50256":-100},
         "logprobs":true,
         "top_logprobs":3}
        """;

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!.AsObject();

        result["stop"]!.AsArray()[0]!.GetValue<string>().ShouldBe("STOP");
        result["metadata"]!["k"]!.GetValue<string>().ShouldBe("v");
        result["logit_bias"]!["50256"]!.GetValue<int>().ShouldBe(-100);
        result["logprobs"]!.GetValue<bool>().ShouldBeTrue();
        result["top_logprobs"]!.GetValue<int>().ShouldBe(3);
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

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!.AsObject();

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

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession));

        ex.Message.ShouldContain("text.format type 'grammar'");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_json_schema_text_format_missing_name_and_schema()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":"hi","text":{"format":{"type":"json_schema","strict":true}}}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession));

        ex.Message.ShouldContain("json_schema requires name and schema");
    }

    [Theory]
    [InlineData("json_object")]
    [InlineData("text")]
    public void OpenAi_chat_upstream_passes_through_simple_responses_text_format(string formatType)
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":"hi","text":{"format":{"type":"FORMAT"}}}""".Replace("FORMAT", formatType);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!.AsObject();

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

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

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

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages.Count.ShouldBe(1);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("hi");
    }

    [Fact]
    public void OpenAi_chat_upstream_drops_assistant_message_with_empty_content()
    {
        var transformer = OpenAi();
        // Codex /responses transcripts can carry an assistant turn whose text is empty (the real content
        // was the function_call beside it). Converting it verbatim emits {"role":"assistant","content":""},
        // which strict Chat upstreams (Moonshot) reject as an empty message. The empty turn is dropped.
        string body = """
        {"model":"gpt5.4","input":[
          {"role":"user","content":[{"type":"input_text","text":"hi"}]},
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":""}]},
          {"role":"user","content":[{"type":"input_text","text":"again"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages.Count.ShouldBe(2);
        messages.Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user", "user"]);
        messages[0]!["content"]!.GetValue<string>().ShouldBe("hi");
        messages[1]!["content"]!.GetValue<string>().ShouldBe("again");
    }

    [Fact]
    public void OpenAi_chat_upstream_drops_message_with_null_content()
    {
        var transformer = OpenAi();
        // A message Item with no content field at all converts to "" and must not reach the wire empty.
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"message","role":"assistant"},
          {"role":"user","content":[{"type":"input_text","text":"hi"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages.Count.ShouldBe(1);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void OpenAi_chat_upstream_drops_message_whose_content_parts_are_all_unsupported()
    {
        var transformer = OpenAi();
        // An assistant turn built only from parts we do not carry over (a refusal) collapses to an empty
        // content array; it must be dropped, not emitted as {"role":"assistant","content":[]}.
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"I can't help with that."}]},
          {"role":"user","content":[{"type":"input_text","text":"hi"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages.Count.ShouldBe(1);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void OpenAi_chat_upstream_keeps_assistant_message_with_text_beside_empty_turn()
    {
        var transformer = OpenAi();
        // Guard against over-dropping: a non-empty assistant message must still survive next to an empty one.
        string body = """
        {"model":"gpt5.4","input":[
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"here is the answer"}]},
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"   "}]},
          {"role":"user","content":[{"type":"input_text","text":"thanks"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages.Count.ShouldBe(2);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("assistant");
        messages[0]!["content"]!.GetValue<string>().ShouldBe("here is the answer");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void OpenAi_chat_upstream_drops_empty_assistant_turn_but_keeps_its_paired_tool_history()
    {
        var transformer = OpenAi();
        // The reported failure shape: an empty assistant message sits beside the function_call/output it
        // accompanied. The empty message is dropped; the paired tool exchange still downgrades to a valid
        // assistant tool_calls + tool transcript.
        string body = """
        {"model":"gpt5.4","input":[
          {"role":"user","content":[{"type":"input_text","text":"weather?"}]},
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":""}]},
          {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"},
          {"type":"function_call_output","call_id":"call_1","output":"sunny"},
          {"role":"user","content":[{"type":"input_text","text":"thanks"}]}
        ]}
        """;

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

        messages.Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user", "assistant", "tool", "user"]);
        messages[1]!["tool_calls"]!.AsArray()[0]!["id"]!.GetValue<string>().ShouldBe("call_1");
        messages[1]!.AsObject().ContainsKey("content").ShouldBeFalse();
        messages[2]!["content"]!.GetValue<string>().ShouldBe("sunny");
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

        JsonArray messages = JsonNode.Parse(transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession))!["messages"]!.AsArray();

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

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession));

        ex.Message.ShouldContain("mcp_list_tools");
    }

    [Fact]
    public void OpenAi_chat_upstream_rejects_unknown_responses_input_item_type()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":[{"type":"mystery_item","role":"user","content":"x"}]}""";

        RoutingException ex = Should.Throw<RoutingException>(() => transformer.Transform(body, ChatDecision("kimi", caching: false), "gpt5.4", NoSession));

        ex.Message.ShouldContain("mystery_item");
    }

    [Fact]
    public void Anthropic_string_system_becomes_block_with_ephemeral_cache_control()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """{"model":"claude-haiku-x","system":"you are helpful","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("claude-3-5-haiku-latest", caching: true), "claude-haiku-x", NoSession))!;

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

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("target", caching: true), "claude-haiku-x", NoSession))!;

        JsonArray content = result["messages"]!.AsArray()[0]!["content"]!.AsArray();
        content[0]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
        content[1]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public void Anthropic_without_caching_only_rewrites_model()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """{"model":"claude-x","system":"sys","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("target", caching: false), "claude-x", NoSession))!;

        result["model"]!.GetValue<string>().ShouldBe("target");
        result["system"]!.GetValueKind().ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public void Anthropic_nested_body_serializes_after_rewrite_and_cache_injection()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """
        {"model":"claude-x","max_tokens":1024,"temperature":0.7,
         "system":[{"type":"text","text":"sys"}],
         "messages":[
           {"role":"user","content":[
             {"type":"text","text":"hi","metadata":{"score":1.25,"flags":[true,false,null]}}
           ]}
         ]}
        """;

        JsonObject result = JsonNode.Parse(transformer.Transform(body, Decision("target", caching: true), "claude-x", NoSession))!.AsObject();

        result["model"]!.GetValue<string>().ShouldBe("target");
        result["max_tokens"]!.GetValue<int>().ShouldBe(1024);
        result["temperature"]!.GetValue<decimal>().ShouldBe(0.7m);
        JsonObject content = result["messages"]!.AsArray()[0]!["content"]!.AsArray()[0]!.AsObject();
        content["metadata"]!["flags"]!.AsArray().Count.ShouldBe(3);
        content["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public void Invalid_json_throws_routing_exception()
    {
        var transformer = OpenAi();
        Should.Throw<RoutingException>(() => transformer.Transform("not json", Decision("x", false), "x", NoSession));
    }

    private static RouteDecision SessionChatDecision(bool isImposter, SessionForwarding forwarding = SessionForwarding.OpencodeGo) =>
        new(
            new ProviderRoute(
                "p", ApiDialect.OpenAi, new Uri("https://p.example"), null, false, null, [],
                OpenAiUpstreamApi.ChatCompletions, null, RequestNormalization.None, true, null, null, forwarding),
            "kimi",
            CachingEnabled: false,
            IsImposter: isImposter);

    private static RouteDecision SessionResponsesDecision(bool caching, SessionForwarding forwarding = SessionForwarding.OpencodeGo) =>
        new(
            new ProviderRoute(
                "p", ApiDialect.OpenAi, new Uri("https://p.example"), null, false, null, [],
                OpenAiUpstreamApi.Responses, null, RequestNormalization.None, true, null, null, forwarding),
            "grok-code",
            CachingEnabled: caching,
            IsImposter: true);

    [Fact]
    public void OpenAi_session_forwarding_stamps_session_id_on_opted_in_imposter()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}""";
        var session = new SessionIdentity("sess-123", SessionIdentitySource.Captured);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, SessionChatDecision(isImposter: true), "gpt5.4", session))!.AsObject();

        result["session_id"]!.GetValue<string>().ShouldBe("sess-123");
    }

    [Fact]
    public void OpenAi_session_forwarding_is_byte_transparent_without_opt_in()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}""";
        var session = new SessionIdentity("sess-123", SessionIdentitySource.Captured);

        JsonObject result = JsonNode.Parse(
            transformer.Transform(body, SessionChatDecision(isImposter: true, SessionForwarding.None), "gpt5.4", session))!.AsObject();

        result.ContainsKey("session_id").ShouldBeFalse();
    }

    [Fact]
    public void OpenAi_session_forwarding_skipped_on_passthrough_even_when_opted_in()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}""";
        var session = new SessionIdentity("sess-123", SessionIdentitySource.Captured);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, SessionChatDecision(isImposter: false), "gpt5.4", session))!.AsObject();

        result.ContainsKey("session_id").ShouldBeFalse();
    }

    [Fact]
    public void OpenAi_responses_to_chat_downgrade_carries_session_id()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":"hi","stream":true}""";
        var session = new SessionIdentity("sess-downgrade", SessionIdentitySource.Captured);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, SessionChatDecision(isImposter: true), "gpt5.4", session))!.AsObject();

        result.ContainsKey("input").ShouldBeFalse();
        result["session_id"]!.GetValue<string>().ShouldBe("sess-downgrade");
        result["messages"]!.AsArray()[0]!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void OpenAi_caching_prefers_session_identity_for_prompt_cache_key()
    {
        var transformer = OpenAi();
        string body = """{"model":"gpt5.4","input":"hi"}""";
        var session = new SessionIdentity("sess-cache", SessionIdentitySource.Derived);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, SessionResponsesDecision(caching: true), "gpt5.4", session))!.AsObject();

        result["prompt_cache_key"]!.GetValue<string>().ShouldBe("sess-cache");
        result["session_id"]!.GetValue<string>().ShouldBe("sess-cache");
    }

    [Fact]
    public void Anthropic_does_not_inject_session_into_body()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """{"model":"claude","messages":[{"role":"user","content":"hi"}]}""";
        var decision = new RouteDecision(
            new ProviderRoute(
                "p", ApiDialect.Anthropic, new Uri("https://p.example"), null, false, null, [],
                OpenAiUpstreamApi.Responses, null, RequestNormalization.None, true, null, null, SessionForwarding.OpencodeGo),
            "claude-out",
            CachingEnabled: false,
            IsImposter: true);
        var session = new SessionIdentity("sess-anthropic", SessionIdentitySource.Captured);

        JsonObject result = JsonNode.Parse(transformer.Transform(body, decision, "claude", session))!.AsObject();

        result["model"]!.GetValue<string>().ShouldBe("claude-out");
        result.ContainsKey("session_id").ShouldBeFalse();
    }
}
