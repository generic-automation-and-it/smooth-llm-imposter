using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmoothLlmImposter.Application.Features.Routing.Bridge;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Bridge;

public class AnthropicChatResponseMapperTests
{
    private static readonly AnthropicChatResponseMapper Mapper = new(NullLogger<AnthropicChatResponseMapper>.Instance);

    private static AnthropicChatCall Call(
        int estimate = 1500,
        string inbound = "claude-sonnet-4-5",
        params string[] toolNames)
    {
        var identity = new AnthropicChatToolIdentity();
        foreach (string name in toolNames)
        {
            identity.RegisterName(name);
        }

        return new AnthropicChatCall(new AnthropicChatRequestResult(
            "{}",
            new AnthropicChatBridgeContext(inbound, false, identity, estimate, new AnthropicChatBridgeOptions())));
    }

    private static JsonObject Map(string chatJson, AnthropicChatCall? call = null)
    {
        JsonObject root = AnthropicChatJson.TryParseObject(chatJson)!;
        return Mapper.MapSuccess(root, (call ?? Call()).Context);
    }

    private static string Compact(JsonNode? node) => AnthropicChatJson.Compact(node);

    private static JsonObject Reply(int status, string body, string? secret = null)
    {
        var bridge = new AnthropicChatBridge(
            new AnthropicChatRequestMapper(NullLogger<AnthropicChatRequestMapper>.Instance),
            Mapper,
            new AnthropicChatStreamTranslator(NullLogger<AnthropicChatStreamTranslator>.Instance),
            TimeProvider.System,
            NullLogger<AnthropicChatBridge>.Instance);
        return AnthropicChatJson.TryParseObject(
            bridge.MapResponse(status, body, null, null, Call(), secret).Body)!;
    }

    private static JsonObject Choice(string content, string? finishReason = null, string? refusal = null, string? reasoning = null) =>
        AnthropicChatJson.TryParseObject(
            $"{{\"choices\":[{{\"index\":0,\"message\":{{\"content\":{content}{(refusal is null ? "" : $",\"refusal\":\"{refusal}\"")}{(reasoning is null ? "" : $",\"reasoning_content\":\"{reasoning}\"")}}},\"finish_reason\":{(finishReason is null ? "null" : $"\"{finishReason}\"")}}}],\"usage\":{{\"prompt_tokens\":100,\"completion_tokens\":10}}}}")!;

    // ---------------------------------------------------------------- 15.1 envelope, choices, text

    [Fact]
    public void Row1_plain_text()
    {
        JsonObject message = Map("""{"choices":[{"index":0,"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":10}}""");
        Compact(message["content"]).ShouldBe("[{\"type\":\"text\",\"text\":\"Hello\"}]");
        Compact(message["stop_reason"]).ShouldBe("\"end_turn\"");
        Compact(message["usage"]).ShouldBe("{\"input_tokens\":100,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":10}");
    }

    [Fact]
    public void Row2_3_fresh_message_id()
    {
        JsonObject a = Map("""{"id":"chatcmpl-1","choices":[{"message":{"content":"x"}}]}""");
        JsonObject b = Map("""{"id":"chatcmpl-1","choices":[{"message":{"content":"x"}}]}""");
        a["id"]!.GetValue<string>().ShouldStartWith("msg_");
        a["id"]!.GetValue<string>().ShouldNotBe(b["id"]!.GetValue<string>());
        a["id"]!.GetValue<string>().ShouldNotBe("chatcmpl-1");
    }

    [Fact]
    public void Row4_5_model_echoes_inbound()
    {
        JsonObject message = Map("""{"model":"gpt-4o-2024-08-06","choices":[{"message":{"content":"x"}}]}""", Call(inbound: "claude-opus-4-5[1m]"));
        message["model"]!.GetValue<string>().ShouldBe("claude-opus-4-5[1m]");
    }

    [Fact]
    public void Row7_8_whitespace_preserved()
    {
        Compact(Map("""{"choices":[{"message":{"content":"  \n  def f():\n    pass\n"}}]}""")["content"]![0]!["text"])
            .ShouldBe("\"  \\n  def f():\\n    pass\\n\"");
        Compact(Map("""{"choices":[{"message":{"content":" \n "}}]}""")["content"]![0]!["text"])
            .ShouldBe("\" \\n \"");
    }

    [Fact]
    public void Row9_10_empty_content()
    {
        Compact(Map("""{"choices":[{"message":{"content":""},"finish_reason":"stop"}]}""")["content"]).ShouldBe("[]");
        Compact(Map("""{"choices":[{"message":{"content":null},"finish_reason":"stop"}]}""")["content"]).ShouldBe("[]");
    }

    [Fact]
    public void Row11_12_13_array_parts()
    {
        Compact(Map("""{"choices":[{"message":{"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}}]}""")["content"]![0]!["text"]).ShouldBe("\"ab\"");
        Compact(Map("""{"choices":[{"message":{"content":["x","y"]}}]}""")["content"]![0]!["text"]).ShouldBe("\"xy\"");
        Compact(Map("""{"choices":[{"message":{"content":[{"type":"image_url","image_url":{}},{"type":"text","text":"z"}]}}]}""")["content"]![0]!["text"]).ShouldBe("\"z\"");
    }

    [Fact]
    public void Row14_content_object_ignored()
    {
        Compact(Map("""{"choices":[{"message":{"content":{"text":"q"}}}]}""")["content"]).ShouldBe("[]");
    }

    [Fact]
    public void Row15_16_refusal()
    {
        JsonObject message = Map("""{"choices":[{"message":{"content":null,"refusal":"I can't help with that."},"finish_reason":"content_filter"}]}""");
        Compact(message["content"]).ShouldBe("[{\"type\":\"text\",\"text\":\"I can't help with that.\"}]");
        Compact(message["stop_reason"]).ShouldBe("\"end_turn\"");
    }

    [Fact]
    public void Row17_18_content_and_refusal()
    {
        Compact(Map("""{"choices":[{"message":{"content":"A","refusal":"B"}}]}""")["content"]![0]!["text"]).ShouldBe("\"A\\nB\"");
        Compact(Map("""{"choices":[{"message":{"content":[{"type":"refusal","refusal":"No."}]}}]}""")["content"]![0]!["text"]).ShouldBe("\"No.\"");
    }

    [Fact]
    public void Row19_think_markup_default_literal()
    {
        Compact(Map("""{"choices":[{"message":{"content":" thinkingx</think>y"}}]}""")["content"]![0]!["text"])
            .ShouldBe("\" thinkingx</think>y\"");
    }

    [Fact]
    public void Row20_empty_choices()
    {
        JsonObject message = Map("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":10}}""");
        Compact(message["content"]).ShouldBe("[]");
        Compact(message["stop_reason"]).ShouldBe("\"end_turn\"");
    }

    [Fact]
    public void Row23_24_selection()
    {
        Compact(Map("""{"choices":[{"index":1,"message":{"content":"B"}},{"index":0,"message":{"content":"A"}}]}""")["content"]![0]!["text"]).ShouldBe("\"A\"");
        Compact(Map("""{"choices":[{"message":{"content":"A"}},{"message":{"content":"B"}}]}""")["content"]![0]!["text"]).ShouldBe("\"A\"");
    }

    [Fact]
    public void Row25_selected_not_object()
    {
        Compact(Map("""{"choices":[null]}""")["content"]).ShouldBe("[]");
    }

    [Fact]
    public void Row26_delta_instead_of_message()
    {
        Compact(Map("""{"choices":[{"delta":{"content":"d"}}]}""")["content"]![0]!["text"]).ShouldBe("\"d\"");
    }

    // ---------------------------------------------------------------- 15.2 reasoning

    [Fact]
    public void Row27_reasoning_block_before_text()
    {
        JsonObject message = Map("""{"choices":[{"message":{"reasoning_content":"think","content":"ans"}}]}""");
        Compact(message["content"]).ShouldBe("[{\"type\":\"thinking\",\"thinking\":\"think\",\"signature\":\"\"},{\"type\":\"text\",\"text\":\"ans\"}]");
    }

    [Fact]
    public void Row28_30_field_precedence()
    {
        Compact(Map("""{"choices":[{"message":{"reasoning_content":"a","reasoning":"b","thinking":"c"}}]}""")["content"]![0]!["thinking"]).ShouldBe("\"a\"");
        Compact(Map("""{"choices":[{"message":{"reasoning_content":"","reasoning":"r"}}]}""")["content"]![0]!["thinking"]).ShouldBe("\"r\"");
        Compact(Map("""{"choices":[{"message":{"reasoning_content":"  ","reasoning":"r"}}]}""")["content"]![0]!["thinking"]).ShouldBe("\"r\"");
    }

    [Fact]
    public void Row33_39_details()
    {
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"type":"reasoning.text","text":"t"}]}}]}""")["content"]![0]!["thinking"]).ShouldBe("\"t\"");
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"type":"reasoning.summary","summary":"s"}]}}]}""")["content"]![0]!["thinking"]).ShouldBe("\"s\"");
        Compact(Map("""{"choices":[{"message":{"reasoning_details":["a","  ","b"]}}]}""")["content"]![0]!["thinking"]).ShouldBe("\"a\\nb\"");
    }

    [Fact]
    public void Row34_35_36_merge()
    {
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"text":"t"}],"reasoning_content":"t"}}]}""")["content"]).ShouldBe("[{\"type\":\"thinking\",\"thinking\":\"t\",\"signature\":\"\"}]");
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"text":"t"}],"reasoning_content":" t\n"}}]}""")["content"]).ShouldBe("[{\"type\":\"thinking\",\"thinking\":\"t\",\"signature\":\"\"}]");
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"text":"d"}],"reasoning_content":"p"}}]}""")["content"]![0]!["thinking"]).ShouldBe("\"p\\nd\"");
    }

    [Fact]
    public void Row40_43_encrypted_and_redacted_dropped()
    {
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"type":"reasoning.encrypted","data":"abc"}],"content":"ok"}}]}""")["content"]).ShouldBe("[{\"type\":\"text\",\"text\":\"ok\"}]");
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"type":"redacted_thinking","data":"z"}],"content":"ok"}}]}""")["content"]).ShouldBe("[{\"type\":\"text\",\"text\":\"ok\"}]");
    }

    [Fact]
    public void Row42_signature_empty()
    {
        Compact(Map("""{"choices":[{"message":{"reasoning_details":[{"type":"reasoning.text","text":"t","signature":"SIG"}]}}]}""")["content"]![0]!["signature"]).ShouldBe("\"\"");
    }

    [Fact]
    public void Row44_reasoning_only()
    {
        JsonObject message = Map("""{"choices":[{"message":{"reasoning_content":"t","content":null}}]}""");
        Compact(message["content"]).ShouldBe("[{\"type\":\"thinking\",\"thinking\":\"t\",\"signature\":\"\"}]");
        Compact(message["stop_reason"]).ShouldBe("\"end_turn\"");
    }

    [Fact]
    public void Think_markup_extraction_option()
    {
        var identity = new AnthropicChatToolIdentity();
        var call = new AnthropicChatCall(new AnthropicChatRequestResult(
            "{}",
            new AnthropicChatBridgeContext("m", false, identity, 1500,
                new AnthropicChatBridgeOptions(ThinkMarkupExtraction: true))));
        JsonObject message = Map("""{"choices":[{"message":{"content":"<thinking>x</thinking>y"}}]}""", call);
        Compact(message["content"]).ShouldBe("[{\"type\":\"thinking\",\"thinking\":\"x\",\"signature\":\"\"},{\"type\":\"text\",\"text\":\"y\"}]");
    }

    // ---------------------------------------------------------------- 15.3 tool calls

    [Fact]
    public void Row47_one_call()
    {
        JsonObject message = Map("""{"choices":[{"message":{"tool_calls":[{"id":"call_1","function":{"name":"Read","arguments":"{\"p\":\"a\"}"}}]},"finish_reason":"tool_calls"}]}""", Call(toolNames: ["Read"]));
        Compact(message["content"]).ShouldBe("[{\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"Read\",\"input\":{\"p\":\"a\"}}]");
        Compact(message["stop_reason"]).ShouldBe("\"tool_use\"");
    }

    [Fact]
    public void Row48_text_and_parallel_calls()
    {
        JsonObject message = Map("""{"choices":[{"message":{"content":"Go","tool_calls":[{"id":"a","function":{"name":"f","arguments":"{}"}},{"id":"b","function":{"name":"g","arguments":"{}"}}]}}]}""", Call(toolNames: ["f", "g"]));
        JsonArray content = message["content"]!.AsArray();
        content.Count.ShouldBe(3);
        Compact(content[1]!["name"]).ShouldBe("\"f\"");
        Compact(content[2]!["name"]).ShouldBe("\"g\"");
    }

    [Fact]
    public void Row49_53_id_rules()
    {
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"function":{"name":"f","arguments":"{}"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["id"])
            .ToString().ShouldStartWith("\"toolu_");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"functions.Bash:0","function":{"name":"f","arguments":"{}"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["id"])
            .ToString().ShouldStartWith("\"toolu_");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"call_1","function":{"name":"f","arguments":"{}"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["id"])
            .ShouldBe("\"call_1\"");
    }

    [Fact]
    public void Row54_55_duplicate_ids()
    {
        JsonObject message = Map("""{"choices":[{"message":{"tool_calls":[{"id":"call_0","function":{"name":"f","arguments":"{}"}},{"id":"call_0","function":{"name":"g","arguments":"{}"}}]}}]}""", Call(toolNames: ["f", "g"]));
        JsonArray content = message["content"]!.AsArray();
        Compact(content[0]!["id"]).ShouldBe("\"call_0\"");
        content[1]!["id"]!.GetValue<string>().ShouldStartWith("toolu_");
    }

    [Fact]
    public void Row57_name_map_reverse()
    {
        JsonObject message = Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"mcp__my_srv__t","arguments":"{}"}}]}}]}""", Call(toolNames: ["mcp__my.srv__t"]));
        Compact(message["content"]![0]!["name"]).ShouldBe("\"mcp__my.srv__t\"");
    }

    [Fact]
    public void Row58_59_unknown_name_passthrough_case_sensitive()
    {
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"Weird","arguments":"{}"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["name"]).ShouldBe("\"Weird\"");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"read","arguments":"{}"}}]}}]}""", Call(toolNames: ["Read"]))["content"]![0]!["name"]).ShouldBe("\"read\"");
    }

    [Fact]
    public void Row61_63_nameless_calls()
    {
        JsonObject withText = Map("""{"choices":[{"message":{"content":"hi","tool_calls":[{"id":"a","function":{"arguments":"{}"}}]}}]}""");
        Compact(withText["content"]).ShouldBe("[{\"type\":\"text\",\"text\":\"hi\"}]");

        JsonObject sole = Reply(200, """{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"arguments":"{}"}}]}}]}""");
        Compact(sole["error"]!["message"]).ShouldBe("\"Upstream returned a tool call without a name.\"");
    }

    [Fact]
    public void Row64_69_argument_sources()
    {
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":""}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["input"]).ShouldBe("{}");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":" \n"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["input"]).ShouldBe("{}");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":{"a":1}}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["input"]).ShouldBe("{\"a\":1}");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","name":"f","input":{"p":1}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["input"]).ShouldBe("{\"p\":1}");
    }

    [Fact]
    public void Row70_72_numbers_compact_duplicates()
    {
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":"{\"a\":1.0,\"b\":1e2,\"c\":12345678901234567890}"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["input"])
            .ShouldBe("{\"a\":1.0,\"b\":1e2,\"c\":12345678901234567890}");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":"{ \"a\" : [ 1 , 2 ] }"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["input"])
            .ShouldBe("{\"a\":[1,2]}");
        Compact(Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":"{\"a\":1,\"a\":2}"}}]}}]}""", Call(toolNames: ["f"]))["content"]![0]!["input"])
            .ShouldBe("{\"a\":2}");
    }

    [Theory]
    [InlineData("{\"a\":\"x", "{\"a\":\"x\"}")]
    [InlineData("{\"a\":[1,2", "{\"a\":[1,2]}")]
    [InlineData("{\"a\":{\"b\":\"c", "{\"a\":{\"b\":\"c\"}}")]
    [InlineData("{\"a\":1,", "{\"a\":1}")]
    public void Row73_77_repair(string arguments, string expected)
    {
        string body = $"{{\"choices\":[{{\"message\":{{\"tool_calls\":[{{\"id\":\"a\",\"function\":{{\"name\":\"f\",\"arguments\":{JsonValue.Create(arguments)!.ToJsonString()}}}}}]}},\"finish_reason\":\"tool_calls\"}}]}}";
        Compact(Map(body, Call(toolNames: ["f"]))["content"]![0]!["input"]).ShouldBe(expected);
    }

    [Theory]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":1,\"b\"")]
    [InlineData("[1,2]")]
    [InlineData("\"x\"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("{\"a\":1} extra")]
    public void Row78_82_unrepairable_fails_502(string arguments)
    {
        string body = $"{{\"choices\":[{{\"message\":{{\"tool_calls\":[{{\"id\":\"a\",\"function\":{{\"name\":\"f\",\"arguments\":{JsonValue.Create(arguments)!.ToJsonString()}}}}}]}},\"finish_reason\":\"tool_calls\"}}]}}";
        JsonObject failure = Reply(200, body);
        Compact(failure["error"]!["type"]).ShouldBe("\"api_error\"");
        Compact(failure["error"]!["message"]).ShouldBe("\"Upstream returned invalid arguments for tool call \\\"f\\\".\"");
    }

    [Fact]
    public void Row83_86_length_and_arguments()
    {
        JsonObject dropped = Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":"{\"a\":"}}]},"finish_reason":"length"}]}""", Call(toolNames: ["f"]));
        Compact(dropped["content"]).ShouldBe("[]");
        Compact(dropped["stop_reason"]).ShouldBe("\"max_tokens\"");

        JsonObject repaired = Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":"{\"a\":\"x"}}]},"finish_reason":"length"}]}""", Call(toolNames: ["f"]));
        Compact(repaired["content"]).ShouldBe("[{\"type\":\"tool_use\",\"id\":\"a\",\"name\":\"f\",\"input\":{\"a\":\"x\"}}]");
        Compact(repaired["stop_reason"]).ShouldBe("\"tool_use\"");
    }

    [Fact]
    public void Row87_89_tool_calls_object_and_legacy()
    {
        Compact(Map("""{"choices":[{"message":{"tool_calls":{"id":"a","function":{"name":"f","arguments":"{}"}}}}]}""", Call(toolNames: ["f"]))["content"]).ShouldBe("[]");

        JsonObject legacy = Map("""{"choices":[{"message":{"content":null,"function_call":{"name":"f","arguments":"{}"}},"finish_reason":"function_call"}]}""", Call(toolNames: ["f"]));
        Compact(legacy["content"]![0]!["name"]).ShouldBe("\"f\"");
        Compact(legacy["stop_reason"]).ShouldBe("\"tool_use\"");

        JsonObject both = Map("""{"choices":[{"message":{"tool_calls":[{"id":"A","function":{"name":"f","arguments":"{}"}}],"function_call":{"name":"g","arguments":"{}"}}}]}""", Call(toolNames: ["f", "g"]));
        Compact(both["content"]).ShouldBe("[{\"type\":\"tool_use\",\"id\":\"A\",\"name\":\"f\",\"input\":{}}]");
    }

    // ---------------------------------------------------------------- 15.4 stop_reason

    [Theory]
    [InlineData("stop", "end_turn")]
    [InlineData("length", "max_tokens")]
    [InlineData("MAX_TOKENS", "max_tokens")]
    [InlineData("max_output_tokens", "max_tokens")]
    [InlineData("model_context_window_exceeded", "max_tokens")]
    [InlineData("content_filter", "end_turn")]
    [InlineData("sensitive", "end_turn")]
    [InlineData("eos", "end_turn")]
    [InlineData(null, "end_turn")]
    public void Row94_103_stop_reason(string? finish, string expected)
    {
        string body = finish is null
            ? """{"choices":[{"message":{"content":"x"}}]}"""
            : $"{{\"choices\":[{{\"message\":{{\"content\":\"x\"}},\"finish_reason\":\"{finish}\"}}]}}";
        Compact(Map(body)["stop_reason"]).ShouldBe($"\"{expected}\"");
    }

    [Fact]
    public void Row95_stop_with_calls_is_tool_use()
    {
        JsonObject message = Map("""{"choices":[{"message":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":"{}"}}]},"finish_reason":"stop"}]}""", Call(toolNames: ["f"]));
        Compact(message["stop_reason"]).ShouldBe("\"tool_use\"");
    }

    // ---------------------------------------------------------------- 15.5 usage

    [Fact]
    public void Row104_standard_cache_read()
    {
        Compact(Map("""{"choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":6,"prompt_tokens_details":{"cached_tokens":1024}}}""")["usage"])
            .ShouldBe("{\"input_tokens\":176,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":1024,\"output_tokens\":6}");
    }

    [Fact]
    public void Row105_106_cached_exceeds_prompt()
    {
        Compact(Map("""{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":60}}}""")["usage"])
            .ShouldBe("{\"input_tokens\":0,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":60,\"output_tokens\":1}");
        Compact(Map("""{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":0}}}""")["usage"])
            .ShouldBe("{\"input_tokens\":10,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}");
    }

    [Fact]
    public void Row107_110_vendor_names()
    {
        Compact(Map("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":1,"prompt_cache_hit_tokens":60,"prompt_cache_miss_tokens":40}}""")["usage"])
            .ShouldBe("{\"input_tokens\":40,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":60,\"output_tokens\":1}");
        Compact(Map("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":1,"cache_read_input_tokens":30,"cache_creation_input_tokens":20}}""")["usage"])
            .ShouldBe("{\"input_tokens\":50,\"cache_creation_input_tokens\":20,\"cache_read_input_tokens\":30,\"output_tokens\":1}");
    }

    [Fact]
    public void Row118_119_usage_absent_estimated()
    {
        JsonObject withText = Map("""{"choices":[{"message":{"content":"Hello there!"}}]}""");
        Compact(withText["usage"]!["input_tokens"]).ShouldBe("1500");
        withText["usage"]!["output_tokens"]!.GetValue<long>().ShouldBeGreaterThan(0);

        JsonObject empty = Map("""{"choices":[{"message":{"content":""}}]}""");
        Compact(empty["usage"]!["input_tokens"]).ShouldBe("1500");
        Compact(empty["usage"]!["output_tokens"]).ShouldBe("0");
    }

    [Fact]
    public void Row122_123_prompt_zero()
    {
        Compact(Map("""{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":0,"completion_tokens":9}}""")["usage"]!["input_tokens"]).ShouldBe("1500");
        Compact(Map("""{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":0,"completion_tokens":9}}""")["usage"]!["output_tokens"]).ShouldBe("9");
    }

    [Fact]
    public void Row125_numeric_strings_absent()
    {
        JsonObject message = Map("""{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":"100","completion_tokens":"10"}}""");
        Compact(message["usage"]!["input_tokens"]).ShouldBe("1500");
        message["usage"]!["output_tokens"]!.GetValue<long>().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Row126_usage_in_choice()
    {
        Compact(Map("""{"choices":[{"message":{"content":"x"},"usage":{"prompt_tokens":7,"completion_tokens":3}}]}""")["usage"]!["input_tokens"]).ShouldBe("7");
    }

    // ---------------------------------------------------------------- 15.6 errors

    [Theory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(404, "not_found_error")]
    [InlineData(408, "timeout_error")]
    [InlineData(413, "request_too_large")]
    [InlineData(429, "rate_limit_error")]
    [InlineData(500, "api_error")]
    [InlineData(502, "api_error")]
    [InlineData(503, "overloaded_error")]
    [InlineData(529, "overloaded_error")]
    [InlineData(422, "invalid_request_error")]
    public void Error_status_maps_type(int status, string type)
    {
        JsonObject envelope = Reply(status, """{"error":{"message":"boom"}}""");
        Compact(envelope["error"]!["type"]).ShouldBe($"\"{type}\"");
    }

    [Fact]
    public void Error_html_and_empty()
    {
        Compact(Reply(502, "<html>bad</html>")["error"]!["message"]).ShouldBe("\"Upstream returned an HTML error page (HTTP 502).\"");
        Compact(Reply(500, "")["error"]!["message"]).ShouldBe("\"Upstream returned HTTP 500 with no error message.\"");
    }

    [Fact]
    public void Error_detail_array()
    {
        Compact(Reply(422, """{"detail":[{"msg":"a"},{"msg":"b"}]}""")["error"]!["message"]).ShouldBe("\"a; b\"");
    }

    [Fact]
    public void Error_credential_prefix_and_redaction()
    {
        Compact(Reply(401, """{"error":{"message":"Incorrect API key provided: sk-secret-123"}}""", "sk-secret-123")["error"]!["message"])
            .ShouldBe("\"The upstream provider rejected the API key configured for this route (HTTP 401): Incorrect API key provided: ***\"");
        Compact(Reply(401, "")["error"]!["message"])
            .ShouldBe("\"The upstream provider rejected the API key configured for this route (HTTP 401)\"");
    }

    [Fact]
    public void In_band_error_numeric_code()
    {
        JsonObject envelope = Reply(200, """{"error":{"code":429,"message":"slow down"}}""");
        Compact(envelope["error"]!["type"]).ShouldBe("\"rate_limit_error\"");
        Compact(envelope["error"]!["message"]).ShouldBe("\"slow down\"");
    }

    [Fact]
    public void In_band_error_no_code_is_502()
    {
        JsonObject envelope = Reply(200, """{"error":{"message":"x"}}""");
        Compact(envelope["error"]!["type"]).ShouldBe("\"api_error\"");
    }

    [Fact]
    public void Success_non_chat_object()
    {
        Compact(Reply(200, """{"ok":true}""")["error"]!["message"]).ShouldBe("\"Upstream returned a response that is not a Chat Completions object.\"");
        Compact(Reply(200, "<html>")["error"]!["message"]).ShouldBe("\"Upstream returned a response that is not a Chat Completions object.\"");
    }

    // ---------------------------------------------------------------- overflow

    [Theory]
    [InlineData("This model's maximum context length is 8192 tokens. However, you requested 9000 tokens (5000 in the messages, 4000 in the completion). Please reduce the length of the messages or completion.", "prompt is too long: 9000 tokens > 8192 maximum")]
    [InlineData("Requested token count exceeds the model's maximum context length of 262144 tokens. You requested a total of 262462 tokens: 230462 tokens from the input messages and 32000 tokens for the completion.", "input length and `max_tokens` exceed context limit: 230462 + 32000 > 262144")]
    [InlineData("maximum context length is 128000 tokens. However, your messages resulted in 150000 tokens.", "prompt is too long: 150000 tokens > 128000 maximum")]
    [InlineData("The input token count (1100000) exceeds the maximum number of tokens allowed (1048575).", "prompt is too long: 1100000 tokens > 1048575 maximum")]
    [InlineData("Your request exceeded model token limit: 262144 (requested: 270000)", "prompt is too long: 270000 tokens > 262144 maximum")]
    [InlineData("litellm.BadRequestError: ContextWindowExceededError: This model's maximum context length is 4097 tokens, however you requested 5360 tokens (4851 in your prompt; 509 for the completion).", "prompt is too long: 4851 tokens > 4097 maximum")]
    [InlineData("ContextWindowExceededError: maximum context window is 131072 tokens. The input length is 130000 tokens and max_tokens is 4096.", "prompt is too long: 134096 tokens > 131072 maximum")]
    [InlineData("Trying to keep the first 6000 tokens when context overflows. However, the model is loaded with context length of only 4096 tokens, which is not enough.", "prompt is too long: 6000 tokens > 4096 maximum")]
    [InlineData("'max_tokens' or 'max_completion_tokens' is too large: 32000. This model's maximum context length is 8192 tokens and your request has 5000 input tokens (32000 > 8192 - 5000).", "prompt is too long: 37000 tokens > 8192 maximum")]
    [InlineData("'max_tokens' or 'max_completion_tokens' is too large: 32000. This model's maximum context length is 8192 tokens and your request has 1000 input tokens (32000 > 8192 - 1000).", "input length and `max_tokens` exceed context limit: 1000 + 32000 > 8192")]
    [InlineData("prompt is too long: 213462 tokens > 200000 maximum", "prompt is too long: 213462 tokens > 200000 maximum")]
    [InlineData("input length and `max_tokens` exceed context limit: 100000 + 8000 > 105000", "input length and `max_tokens` exceed context limit: 100000 + 8000 > 105000")]
    [InlineData("input length and `max_tokens` exceed context limit: 104000 + 8000 > 105000", "prompt is too long: 112000 tokens > 105000 maximum")]
    [InlineData("maximum context length is 8,192 tokens. However, you requested 9,000 tokens (8,000 in the messages, 1,000 in the completion).", "prompt is too long: 9000 tokens > 8192 maximum")]
    public void Overflow_forms(string upstreamMessage, string expected)
    {
        JsonObject envelope = Reply(400, $"{{\"error\":{{\"message\":{JsonValue.Create(upstreamMessage)!.ToJsonString()}}}}}");
        Compact(envelope["error"]!["type"]).ShouldBe("\"invalid_request_error\"");
        Compact(envelope["error"]!["message"]).ShouldBe($"\"{expected}\"");
    }

    [Fact]
    public void Overflow_no_numbers_text_form()
    {
        Compact(Reply(400, """{"error":{"code":"context_length_exceeded","message":"Request too long."}}""")["error"]!["message"])
            .ShouldBe("\"prompt is too long: Request too long.\"");
    }

    [Fact]
    public void Overflow_inconsistent_numbers_fall_back_to_free_text()
    {
        const string message = "maximum context length is 8192 tokens ... (100 in the messages, 50 in the completion)";
        JsonObject envelope = Reply(400, $"{{\"error\":{{\"message\":{JsonValue.Create(message)!.ToJsonString()}}}}}");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("prompt is too long: " + message);
    }

    [Fact]
    public void Overflow_text_already_prefixed_is_kept_without_second_prefix()
    {
        JsonObject envelope = Reply(400, """{"error":{"message":"prompt is too long"}}""");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("prompt is too long");
    }

    [Fact]
    public void Overflow_not_applied_to_429()
    {
        JsonObject envelope = Reply(429, """{"error":{"message":"token limit exceeded"}}""");
        Compact(envelope["error"]!["type"]).ShouldBe("\"rate_limit_error\"");
    }
    // ---------------------------------------------------------------- regression coverage (RL-003/RL-014/RL-016)

    [Fact]
    public void Row157_choice_level_error_is_in_band()
    {
        JsonObject envelope = Reply(200, """{"choices":[{"error":{"message":"m"},"finish_reason":"error"}]}""");
        Compact(envelope["error"]!["type"]).ShouldBe("\"api_error\"");
        Compact(envelope["error"]!["message"]).ShouldBe("\"m\"");
    }

    [Fact]
    public void Choice_level_error_beside_text_is_ignored()
    {
        JsonObject message = Map("""{"choices":[{"error":{"message":"m"},"message":{"content":"hi"},"finish_reason":"stop"}]}""");
        Compact(message["content"]).ShouldBe("[{\"type\":\"text\",\"text\":\"hi\"}]");
    }

    [Fact]
    public void In_band_overflow_with_numeric_status_is_reworded()
    {
        JsonObject envelope = Reply(200, """{"error":{"code":500,"message":"maximum context length is 8192 tokens"}}""");
        Compact(envelope["error"]!["type"]).ShouldBe("\"invalid_request_error\"");
        envelope["error"]!["message"]!.GetValue<string>().ShouldStartWith("prompt is too long");
    }

    [Fact]
    public void In_band_non_overflow_keeps_status_and_type()
    {
        JsonObject envelope = Reply(200, """{"error":{"code":500,"message":"boom"}}""");
        Compact(envelope["error"]!["type"]).ShouldBe("\"api_error\"");
        Compact(envelope["error"]!["message"]).ShouldBe("\"boom\"");
    }

    [Fact]
    public void In_band_error_redacts_the_configured_key()
    {
        JsonObject envelope = Reply(200, """{"error":{"code":500,"message":"key sk-secret-123 leaked"}}""", "sk-secret-123");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("key *** leaked");
    }

    [Fact]
    public void Choice_level_error_redacts_the_configured_key()
    {
        JsonObject envelope = Reply(200, """{"choices":[{"error":{"message":"key sk-secret-123 leaked"}}]}""", "sk-secret-123");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("key *** leaked");
    }

    [Fact]
    public void In_band_error_redacts_a_short_configured_key()
    {
        JsonObject envelope = Reply(200, """{"error":{"code":500,"message":"key abc leaked"}}""", "abc");
        envelope["error"]!["message"]!.GetValue<string>().ShouldBe("key *** leaked");
    }

    [Fact]
    public void Choice_level_overflow_uses_top_level_numeric_members()
    {
        JsonObject envelope = Reply(200, """{"choices":[{"error":{"code":"context_length_exceeded","n_prompt_tokens":5000,"n_ctx":4096}}]}""");
        Compact(envelope["error"]!["type"]).ShouldBe("\"invalid_request_error\"");
        Compact(envelope["error"]!["message"]).ShouldBe("\"prompt is too long: 5000 tokens > 4096 maximum\"");
    }

}
