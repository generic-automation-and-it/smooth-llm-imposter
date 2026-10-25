using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmoothLlmImposter.Application.Features.Routing.Bridge;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Bridge;

public class AnthropicChatRequestMapperTests
{
    private static readonly AnthropicChatRequestMapper Mapper = new(NullLogger<AnthropicChatRequestMapper>.Instance);

    private static AnthropicChatRequestResult Map(
        string body,
        AnthropicChatBridgeOptions? options = null,
        string inboundModel = "claude-sonnet-4-5") =>
        Mapper.Map(body, inboundModel, options ?? new AnthropicChatBridgeOptions());

    private static JsonObject MapMessages(string messages, string extra = "", AnthropicChatBridgeOptions? options = null) =>
        AnthropicChatJson.TryParseObject(Map($"{{\"model\":\"m\",\"max_tokens\":100,\"messages\":{messages}{extra}}}", options).ChatBody)!;

    private static void Reject(string body, string message, AnthropicChatBridgeOptions? options = null)
    {
        var ex = Should.Throw<AnthropicChatRequestException>(() => Map(body, options));
        ex.Message.ShouldBe(message);
    }

    private static string Compact(JsonNode? node) => AnthropicChatJson.Compact(node);

    // ---------------------------------------------------------------- 18.1 validation

    [Fact]
    public void V01_body_array() => Reject("[]", "Request body must be a JSON object.");

    [Fact]
    public void V02_model_missing() => Reject("""{"max_tokens":1,"messages":[{"role":"user","content":"x"}]}""", "model: Field required");

    [Fact]
    public void V03_model_wrong_type() => Reject("""{"model":5,"max_tokens":1,"messages":[{"role":"user","content":"x"}]}""", "model: Input should be a valid string");

    [Fact]
    public void V04_max_tokens_missing() => Reject("""{"model":"m","messages":[{"role":"user","content":"x"}]}""", "max_tokens: Field required");

    [Fact]
    public void V05_max_tokens_zero() => Reject("""{"model":"m","max_tokens":0,"messages":[{"role":"user","content":"x"}]}""", "max_tokens: Input should be a valid integer greater than or equal to 1");

    [Fact]
    public void V06_max_tokens_fractional() => Reject("""{"model":"m","max_tokens":10.5,"messages":[{"role":"user","content":"x"}]}""", "max_tokens: Input should be a valid integer greater than or equal to 1");

    [Fact]
    public void V07_stream_string() => Reject("""{"model":"m","max_tokens":1,"stream":"true","messages":[{"role":"user","content":"x"}]}""", "stream: Input should be a valid boolean");

    [Fact]
    public void V08_temperature_string() => Reject("""{"model":"m","max_tokens":1,"temperature":"0.5","messages":[{"role":"user","content":"x"}]}""", "temperature: Input should be a valid number");

    [Fact]
    public void V09_stop_sequences_string() => Reject("""{"model":"m","max_tokens":1,"stop_sequences":"END","messages":[{"role":"user","content":"x"}]}""", "stop_sequences: Input should be a valid list");

    [Fact]
    public void V10_stop_sequences_bad_element() => Reject("""{"model":"m","max_tokens":1,"stop_sequences":["a",1],"messages":[{"role":"user","content":"x"}]}""", "stop_sequences.1: Input should be a valid string");

    [Fact]
    public void V11_messages_missing() => Reject("""{"model":"m","max_tokens":1}""", "messages: Field required");

    [Fact]
    public void V12_messages_empty() => Reject("""{"model":"m","max_tokens":1,"messages":[]}""", "messages: at least one message is required");

    [Fact]
    public void V13_whitespace_only_message() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"  \n"}]}""", "messages: no message contains usable content");

    [Fact]
    public void V14_midturn_system_keeps_position()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"a"},{"role":"system","content":"note"},{"role":"user","content":"b"}]""");
        JsonArray messages = body["messages"]!.AsArray();
        messages.Count.ShouldBe(3);
        Compact(messages[1]!["content"]).ShouldBe("\"note\"");
    }

    [Fact]
    public void V15_content_null() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":null}]}""", "messages.0.content: Field required");

    [Fact]
    public void V16_content_element_not_object() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":["hi"]}]}""", "messages.0.content.0: Input should be a valid dictionary");

    [Fact]
    public void V17_block_without_type() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"text":"x"}]}]}""", "messages.0.content.0.type: Field required");

    [Fact]
    public void V18_system_object() => Reject("""{"model":"m","max_tokens":1,"system":{"type":"text","text":"x"},"messages":[{"role":"user","content":"x"}]}""", "system: Input should be a valid string or list");

    [Fact]
    public void V19_system_only_is_not_enough() => Reject("""{"model":"m","max_tokens":1,"system":"S","messages":[]}""", "messages: at least one message is required");

    [Fact]
    public void V20_first_violation_wins() => Reject("""{"model":"m","max_tokens":1,"temperature":"x","messages":[]}""", "temperature: Input should be a valid number");

    // ---------------------------------------------------------------- 18.2 field policy

    [Fact]
    public void P01_document_placeholder_merges_with_text()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"see"},{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"AAA"}}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"see\\n[omitted: unsupported content block 'document']\"");
    }

    [Fact]
    public void P02_server_tool_rejected()
    {
        Reject(
            """{"model":"m","max_tokens":1,"tools":[{"type":"custom","name":"a"},{"type":"custom","name":"b"},{"type":"web_search_20250305","name":"web_search"}],"messages":[{"role":"user","content":"x"}]}""",
            "tools.2.type: \"web_search_20250305\" server tools are not supported");
    }

    [Theory]
    [InlineData("computer_20250124")]
    [InlineData("bash_20250124")]
    [InlineData("text_editor_20250728")]
    [InlineData("memory_20250818")]
    [InlineData("code_execution_20250522")]
    [InlineData("web_fetch_20250910")]
    public void P03_typed_tools_rejected(string type)
    {
        Reject(
            $"{{\"model\":\"m\",\"max_tokens\":1,\"tools\":[{{\"type\":\"{type}\",\"name\":\"t\"}}],\"messages\":[{{\"role\":\"user\",\"content\":\"x\"}}]}}",
            $"tools.0.type: \"{type}\" server tools are not supported");
    }

    [Fact]
    public void P04_assistant_server_tool_placeholder()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"server_tool_use","id":"s"}]}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"[omitted: unsupported content block 'server_tool_use']\"");
    }

    [Fact]
    public void P05_user_server_tool_result_placeholder()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"web_search_tool_result","x":1}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"[omitted: unsupported content block 'web_search_tool_result']\"");
    }

    [Fact]
    public void P06_tool_search_removed_custom_kept()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"type":"tool_search_tool_regex_20251119","name":"tool_search_tool_regex"},{"type":"custom","name":"Read"}]""");
        body["tools"]!.AsArray().Count.ShouldBe(1);
        Compact(body["tools"]![0]!["function"]!["name"]).ShouldBe("\"Read\"");
    }

    [Fact]
    public void P07_cache_control_absent_by_default()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"x","cache_control":{"type":"ephemeral"}}]}]""");
        body["messages"]![0]!["content"]!.ToJsonString().ShouldNotContain("cache_control");
    }

    [Fact]
    public void P08_dropped_fields()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "top_k":5,"service_tier":"auto","context_management":{},"container":{},"mcp_servers":[],"betas":[],"unknown":1""");
        foreach (string field in (string[])["top_k", "service_tier", "context_management", "container", "mcp_servers", "betas", "unknown"])
        {
            body.ContainsKey(field).ShouldBeFalse(field);
        }
    }

    [Fact]
    public void P09_user_id_off_by_default()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "metadata":{"user_id":"u1"}""");
        body.ContainsKey("user").ShouldBeFalse();
    }

    [Fact]
    public void P10_user_id_passthrough()
    {
        var options = new AnthropicChatBridgeOptions(UserIdMode: AnthropicChatUserIdMode.Passthrough);

        Compact(MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "metadata":{"user_id":"u1"}""", options)["user"]).ShouldBe("\"u1\"");
        MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "metadata":{}""", options).ContainsKey("user").ShouldBeFalse();
        MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "metadata":{"user_id":""}""", options).ContainsKey("user").ShouldBeFalse();
        MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "metadata":{"user_id":5}""", options).ContainsKey("user").ShouldBeFalse();
    }

    [Fact]
    public void P11_user_id_hash_vector()
    {
        var options = new AnthropicChatBridgeOptions(UserIdMode: AnthropicChatUserIdMode.Hash);
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "metadata":{"user_id":"user_0f1e_account_9a8b_session_7c6d"}""", options);
        Compact(body["user"]).ShouldBe("\"f1875a946a35b4b918e331e8cc32f68f\"");
    }

    [Fact]
    public void P12_user_id_off_explicit()
    {
        var options = new AnthropicChatBridgeOptions(UserIdMode: AnthropicChatUserIdMode.Off);
        MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "metadata":{"user_id":"u1"}""", options)
            .ContainsKey("user").ShouldBeFalse();
    }

    [Fact]
    public void P13_citations_ignored()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"x","citations":[{"a":1}]}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"x\"");
    }

    [Fact]
    public void P14_ignored_tool_keys_and_non_boolean_strict()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"t","defer_loading":true,"eager_input_streaming":true,"strict":"yes","input_schema":{"type":"object","properties":{}}}]""");
        JsonNode tool = body["tools"]![0]!;
        tool["defer_loading"].ShouldBeNull();
        tool["eager_input_streaming"].ShouldBeNull();
        tool["function"]!["strict"].ShouldBeNull();
    }

    [Fact]
    public void P16_custom_type_accepted()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"type":"custom","name":"x","input_schema":{"type":"object","properties":{}}}]""");
        body["tools"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void P17_tool_without_name() => Reject("""{"model":"m","max_tokens":1,"tools":[{"input_schema":{}}],"messages":[{"role":"user","content":"x"}]}""", "tools.0.name: Field required");

    [Fact]
    public void P18_input_schema_wrong_kind() => Reject("""{"model":"m","max_tokens":1,"tools":[{"name":"t","input_schema":"x"}],"messages":[{"role":"user","content":"x"}]}""", "tools.0.input_schema: Input should be a valid dictionary");

    [Fact]
    public void P19_mcp_toolset_rejected() => Reject("""{"model":"m","max_tokens":1,"tools":[{"type":"mcp_toolset","name":"x"}],"messages":[{"role":"user","content":"x"}]}""", "tools.0.type: \"mcp_toolset\" server tools are not supported");

    [Theory]
    [InlineData("search_result", "\"see\\n[omitted: unsupported content block 'search_result']\"")]
    [InlineData("foo", "\"see\\n[omitted: unsupported content block 'foo']\"")]
    public void P20_P23_unknown_user_blocks(string type, string expected)
    {
        JsonObject body = MapMessages(
            $"[{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"see\"}},{{\"type\":\"{type}\",\"x\":1}}]}}]");
        Compact(body["messages"]![0]!["content"]).ShouldBe(expected);
    }

    [Fact]
    public void P21_document_only_keeps_message()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"A"}}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"[omitted: unsupported content block 'document']\"");
    }

    [Fact]
    public void P22_two_documents()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"document"},{"type":"document"}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"[omitted: unsupported content block 'document']\\n[omitted: unsupported content block 'document']\"");
    }

    [Fact]
    public void P24_interleaved_text_and_document()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"a"},{"type":"document"},{"type":"text","text":"b"}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"a\\n[omitted: unsupported content block 'document']\\nb\"");
    }

    [Fact]
    public void P25_assistant_text_and_mcp_tool_use()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"text","text":"ok"},{"type":"mcp_tool_use","id":"m"}]}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"ok\\n[omitted: unsupported content block 'mcp_tool_use']\"");
    }

    [Fact]
    public void P26_assistant_tool_use_and_document()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"A","input":{}},{"type":"document"}]}]""");
        JsonObject message = body["messages"]![1]!.AsObject();
        Compact(message["content"]).ShouldBe("\"[omitted: unsupported content block 'document']\"");
        message["tool_calls"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void P27_placeholder_type_verbatim()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"a'b"}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"[omitted: unsupported content block 'a'b']\"");
    }

    [Fact]
    public void P90_forward_cache_control()
    {
        var options = new AnthropicChatBridgeOptions(ForwardCacheControl: true);
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"x","cache_control":{"type":"ephemeral"}}]}]""", options: options);
        Compact(body["messages"]![0]!["content"]![0]!["cache_control"]).ShouldBe("{\"type\":\"ephemeral\"}");
    }

    [Fact]
    public void P91_response_format()
    {
        var options = new AnthropicChatBridgeOptions(SendResponseFormat: true);
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""",
            extra: ", \"output_config\":{\"format\":{\"type\":\"json_schema\",\"schema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"object\",\"properties\":{}}}}}}",
            options: options);
        JsonNode format = body["response_format"]!;
        Compact(format["type"]).ShouldBe("\"json_schema\"");
        Compact(format["json_schema"]!["name"]).ShouldBe("\"response\"");
        Compact(format["json_schema"]!["schema"]!["additionalProperties"]).ShouldBe("false");
        Compact(format["json_schema"]!["schema"]!["properties"]!["a"]!["additionalProperties"]).ShouldBe("false");
    }

    [Fact]
    public void P92_response_format_default_absent()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""",
            extra: """, "output_config":{"format":{"type":"json_schema","schema":{"type":"object"}}}""");
        body.ContainsKey("response_format").ShouldBeFalse();
    }

    // ---------------------------------------------------------------- 18.3 system

    [Fact]
    public void S01_system_no_trim()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: ", \"system\":\"  Be brief.\\n\"");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"  Be brief.\\n\"");
    }

    [Fact]
    public void S02_attribution_dropped()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""",
            extra: """, "system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.0.a1b; cc_entrypoint=cli;"},{"type":"text","text":"identity"},{"type":"text","text":"instructions"}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"identity\\ninstructions\"");
    }

    [Fact]
    public void S03_marker_second_kept()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""",
            extra: """, "system":[{"type":"text","text":"a"},{"type":"text","text":"x-anthropic-billing-header: z"}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"a\\nx-anthropic-billing-header: z\"");
    }

    [Fact]
    public void S04_string_marker_kept()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: ", \"system\":\"x-anthropic-billing-header: z\"");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"x-anthropic-billing-header: z\"");
    }

    [Fact]
    public void S05_only_marker_no_system()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "system":[{"type":"text","text":"x-anthropic-billing-header: z"}]""");
        body["messages"]![0]!["role"]!.ToJsonString().ShouldBe("\"user\"");
    }

    [Fact]
    public void S06_marker_case_insensitive()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "system":[{"type":"text","text":"X-Anthropic-Billing-Header: cc_version=1"},{"type":"text","text":"b"}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"b\"");
    }

    [Fact]
    public void S07_leading_space_not_marker()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "system":[{"type":"text","text":" x-anthropic-billing-header: z"}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\" x-anthropic-billing-header: z\"");
    }

    [Fact]
    public void S08_non_text_block_placeholder()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "system":[{"type":"text","text":"  "},{"type":"image","source":{"type":"url","url":"https://x"}}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"[omitted: unsupported content block 'image']\"");
    }

    // ---------------------------------------------------------------- 18.4 messages

    [Fact]
    public void M01_text_joined()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"A\n"},{"type":"text","text":"B"}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"A\\n\\nB\"");
    }

    [Fact]
    public void M02_whitespace_preserved()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"  x"}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"  x\"");
    }

    [Fact]
    public void M03_whitespace_message_removed_not_merged()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"a"},{"role":"user","content":"   "},{"role":"user","content":"b"}]""");
        body["messages"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void M04_consecutive_users_not_merged()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"a"},{"role":"user","content":"b"}]""");
        body["messages"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void M05_assistant_text_joined()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"a\\nb\"");
    }

    [Fact]
    public void M06_assistant_prefill()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":"{"}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"{\"");
    }

    [Fact]
    public void M07_assistant_image_only_removed()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"image","source":{"type":"url","url":"https://x"}}]}]""");
        body["messages"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void M08_thinking_in_user_dropped()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"thinking","thinking":"t"},{"type":"text","text":"x"}]}]""");
        Compact(body["messages"]![0]!["content"]).ShouldBe("\"x\"");
    }

    [Fact]
    public void M09_misplaced_blocks_placeholder()
    {
        JsonObject assistant = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_result","tool_use_id":"t","content":"x"}]}]""");
        Compact(assistant["messages"]![1]!["content"]).ShouldBe("\"[omitted: unsupported content block 'tool_result']\"");

        JsonObject user = MapMessages("""[{"role":"user","content":[{"type":"tool_use","id":"t","name":"A","input":{}}]}]""");
        Compact(user["messages"]![0]!["content"]).ShouldBe("\"[omitted: unsupported content block 'tool_use']\"");
    }

    [Fact]
    public void M10_byte_identical()
    {
        const string body = """{"model":"m","max_tokens":100,"messages":[{"role":"user","content":[{"type":"text","text":"a"}]}]}""";
        Map(body).ChatBody.ShouldBe(Map(body).ChatBody);
    }

    [Fact]
    public void M91_merge_into_user()
    {
        var options = new AnthropicChatBridgeOptions(MidturnSystem: AnthropicChatMidturnSystem.MergeIntoUser);
        JsonObject body = MapMessages("""[{"role":"user","content":"a"},{"role":"system","content":"note"},{"role":"user","content":"b"}]""", options: options);
        JsonArray messages = body["messages"]!.AsArray();
        messages.Count.ShouldBe(2);
        Compact(messages[1]!["content"]).ShouldBe("\"note\\nb\"");
    }

    [Fact]
    public void M92_system_image_placeholder()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"system","content":[{"type":"image","source":{"type":"url","url":"https://x"}}]}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"[omitted: unsupported content block 'image']\"");
    }

    // ---------------------------------------------------------------- 18.5 images

    [Fact]
    public void I01_image_base64()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"text","text":"look"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]""");
        JsonArray content = body["messages"]![0]!["content"]!.AsArray();
        Compact(content[1]).ShouldBe("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAAA\"}}");
    }

    [Fact]
    public void I03_url_image()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://x/y.png"}}]}]""");
        Compact(body["messages"]![0]!["content"]![0]!["image_url"]!["url"]).ShouldBe("\"https://x/y.png\"");
    }

    [Fact]
    public void I04_media_type_lowercased()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"IMAGE/PNG","data":"A"}}]}]""");
        Compact(body["messages"]![0]!["content"]![0]!["image_url"]!["url"]).ShouldBe("\"data:image/png;base64,A\"");
    }

    [Fact]
    public void I05_source_type_invalid() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"type":"image","source":{"type":"file","file":"f"}}]}]}""", "messages.0.content.0.source.type: Input should be 'base64' or 'url'");

    [Fact]
    public void I06_pdf_image_rejected() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"application/pdf","data":"A"}}]}]}""", "messages.0.content.0.source.media_type: Input should be an image media type");

    [Fact]
    public void I07_data_missing() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png"}}]}]}""", "messages.0.content.0.source.data: Field required");

    [Fact]
    public void N90_document_as_image()
    {
        var options = new AnthropicChatBridgeOptions(DocumentMode: AnthropicChatDocumentMode.Image);
        JsonObject body = MapMessages("""[{"role":"user","content":[{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"AAA"}}]}]""", options: options);
        Compact(body["messages"]![0]!["content"]![0]!["image_url"]!["url"]).ShouldBe("\"data:application/pdf;base64,AAA\"");
    }

    // ---------------------------------------------------------------- 18.6 tool_use / tool_result

    private const string CallA = """{"type":"tool_use","id":"t1","name":"Read","input":{"a":1}}""";

    [Fact]
    public void T01_tool_call_and_result()
    {
        JsonObject body = MapMessages($$"""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"text","text":"ok"},{{CallA}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"done"}]}]""");
        JsonObject assistant = body["messages"]![1]!.AsObject();
        Compact(assistant["content"]).ShouldBe("\"ok\"");
        Compact(assistant["tool_calls"]![0]).ShouldBe("{\"id\":\"t1\",\"type\":\"function\",\"function\":{\"name\":\"Read\",\"arguments\":\"{\\\"a\\\":1}\"}}");
        Compact(body["messages"]![2]).ShouldBe("{\"role\":\"tool\",\"tool_call_id\":\"t1\",\"content\":\"done\"}");
    }

    [Fact]
    public void T02_results_in_call_order()
    {
        JsonObject body = MapMessages("""
            [{"role":"user","content":"x"},
             {"role":"assistant","content":[{"type":"tool_use","id":"A","name":"f","input":{}},{"type":"tool_use","id":"B","name":"g","input":{}}]},
             {"role":"user","content":[{"type":"tool_result","tool_use_id":"B","content":"b"},{"type":"tool_result","tool_use_id":"A","content":"a"}]}]
            """);
        JsonArray messages = body["messages"]!.AsArray();
        messages[2]!["tool_call_id"]!.ToJsonString().ShouldBe("\"A\"");
        messages[3]!["tool_call_id"]!.ToJsonString().ShouldBe("\"B\"");
    }

    [Fact]
    public void T05_orphan_result_user_text()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"user","content":[{"type":"tool_result","tool_use_id":"X","content":"done"}]}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"Tool result (tool_use_id: X):\\ndone\"");
    }

    [Fact]
    public void T09_string_content_verbatim()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"  line\n"}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"  line\\n\"");
    }

    [Fact]
    public void T11_is_error_prefix()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","is_error":true,"content":"File not found"}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"Error: File not found\"");
    }

    [Fact]
    public void T12_is_error_no_content()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","is_error":true}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"Error: (no details)\"");
    }

    [Fact]
    public void T13_empty_content()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t"}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"\"");
    }

    [Fact]
    public void T15_image_hoisted()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"C1","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"C1","content":[{"type":"text","text":"Image file read."},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"A"}}]}]}]""");
        JsonArray messages = body["messages"]!.AsArray();
        Compact(messages[2]!["content"]).ShouldBe("\"Image file read.\"");
        Compact(messages[3]!["content"]![0]!["text"]).ShouldBe("\"Images returned by tool call C1:\"");
    }

    [Fact]
    public void T16_image_only_result_text()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"C1","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"C1","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"A"}}]}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"(tool returned an image; see the next message)\"");
    }

    [Fact]
    public void T20_tool_reference_only()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"tool_reference","tool_name":"my.tool"}]}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"[{\\\"type\\\":\\\"tool_reference\\\",\\\"tool_name\\\":\\\"my_tool\\\"}]\"");
    }

    [Fact]
    public void T25_non_object_input_rejected() => Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":"{\"x\":1}"}]}]}""", "messages.1.content.0.input: Input should be a valid dictionary");

    [Fact]
    public void T26_numbers_keep_text()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{"a":1.0,"b":12345678901234567890,"c":1e2}}]}]""");
        Compact(body["messages"]![1]!["tool_calls"]![0]!["function"]!["arguments"]).ShouldBe("\"{\\\"a\\\":1.0,\\\"b\\\":12345678901234567890,\\\"c\\\":1e2}\"");
    }

    [Fact]
    public void T27_key_order_and_no_escaping()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{"b":1,"a":"<é>"}}]}]""");
        Compact(body["messages"]![1]!["tool_calls"]![0]!["function"]!["arguments"]).ShouldBe("\"{\\\"b\\\":1,\\\"a\\\":\\\"<é>\\\"}\"");
    }

    [Fact]
    public void T28_illegal_id_hashed()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"call.1:abc","name":"f","input":{}}]}]""");
        string id = body["messages"]![1]!["tool_calls"]![0]!["id"]!.GetValue<string>();
        id.ShouldStartWith("toolu_");
        id.Length.ShouldBe(6 + 24);
        id[6..].All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c)).ShouldBeTrue();
    }

    [Fact]
    public void T29_tool_only_content_empty()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"\"");
    }

    [Fact]
    public void T90_illegal_id_matches_result()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"call.1:abc","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call.1:abc","content":"ok"}]}]""");
        string callId = body["messages"]![1]!["tool_calls"]![0]!["id"]!.GetValue<string>();
        body["messages"]![2]!["tool_call_id"]!.GetValue<string>().ShouldBe(callId);
    }

    [Fact]
    public void T91_duplicate_ids()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"dup","name":"f","input":{}},{"type":"tool_use","id":"dup","name":"g","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"dup","content":"ok"}]}]""");
        JsonArray calls = body["messages"]![1]!["tool_calls"]!.AsArray();
        calls[0]!["id"]!.GetValue<string>().ShouldBe("dup");
        calls[1]!["id"]!.GetValue<string>().ShouldBe("dup_2");
        body["messages"]![2]!["tool_call_id"]!.GetValue<string>().ShouldBe("dup");
    }

    // ---------------------------------------------------------------- 18.7 names and tools

    [Fact]
    public void N01_name_sanitized()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"my tool.v2"}]""");
        body["tools"]![0]!["function"]!["name"]!.GetValue<string>().ShouldBe("my_tool_v2");
    }

    [Fact]
    public void N02_collision_hash()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"a  b"},{"name":"a...b"}]""");
        body["tools"]![0]!["function"]!["name"]!.GetValue<string>().ShouldBe("a_b");
        body["tools"]![1]!["function"]!["name"]!.GetValue<string>().ShouldBe("a_b_eaf7617e");
    }

    [Fact]
    public void N04_non_ascii_name()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"日本語"}]""");
        body["tools"]![0]!["function"]!["name"]!.GetValue<string>().ShouldBe("_");
    }

    [Fact]
    public void N05_non_ascii_collision()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"日本語"},{"name":"日本"}]""");
        body["tools"]![0]!["function"]!["name"]!.GetValue<string>().ShouldBe("_");
        body["tools"]![1]!["function"]!["name"]!.GetValue<string>().ShouldBe("__cf2abf0c");
    }

    [Fact]
    public void N06_underscore_collision()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"a.b"},{"name":"a_b"}]""");
        body["tools"]![0]!["function"]!["name"]!.GetValue<string>().ShouldBe("a_b");
        body["tools"]![1]!["function"]!["name"]!.GetValue<string>().ShouldBe("a_b_648fa9b3");
    }

    [Fact]
    public void N07_long_name_truncated_with_hash()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"mcp__averyverylongserver__averyverylongtoolname_that_goes_past_the_limit_of_64"}]""");
        string name = body["tools"]![0]!["function"]!["name"]!.GetValue<string>();
        name.Length.ShouldBe(64);
        name.ShouldEndWith("_3609e2a4");
    }

    [Fact]
    public void N08_history_only_name_extends_map()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"unlisted tool!","input":{}}]}]""");
        body["messages"]![1]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>().ShouldBe("unlisted_tool_");
    }

    [Fact]
    public void N10_duplicate_tool_first_wins()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"Read","description":"first"},{"name":"Read","description":"second"}]""");
        body["tools"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void L02_description_omitted_when_empty()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"t","description":""}]""");
        body["tools"]![0]!["function"]!["description"].ShouldBeNull();
    }

    [Fact]
    public void L03_default_schema()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"t"}]""");
        Compact(body["tools"]![0]!["function"]!["parameters"]).ShouldBe("{\"type\":\"object\",\"properties\":{}}");
    }

    [Fact]
    public void I90_strict_boolean_forwarded()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"t","strict":true}]""");
        Compact(body["tools"]![0]!["function"]!["strict"]).ShouldBe("true");
    }

    // ---------------------------------------------------------------- 18.8 tool_choice

    [Theory]
    [InlineData("""{"type":"auto"}""", "\"auto\"")]
    [InlineData("""{"type":"any"}""", "\"required\"")]
    [InlineData("""{"type":"none"}""", "\"none\"")]
    [InlineData("\"any\"", "\"required\"")]
    [InlineData("\"required\"", "\"required\"")]
    [InlineData("\"auto\"", "\"auto\"")]
    [InlineData("\"none\"", "\"none\"")]
    public void C01_C08_tool_choice_normalisation(string choice, string expected)
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: ", \"tools\":[{\"name\":\"t\"}],\"tool_choice\":" + choice);
        Compact(body["tool_choice"]).ShouldBe(expected);
    }

    [Fact]
    public void C04_forced_tool_mapped()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"my tool.v2"}],"tool_choice":{"type":"tool","name":"my tool.v2"}""");
        Compact(body["tool_choice"]).ShouldBe("{\"type\":\"function\",\"function\":{\"name\":\"my_tool_v2\"}}");
    }

    [Fact]
    public void C05_parallel_disabled()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"t"}],"tool_choice":{"type":"auto","disable_parallel_tool_use":true}""");
        Compact(body["tool_choice"]).ShouldBe("\"auto\"");
        Compact(body["parallel_tool_calls"]).ShouldBe("false");
    }

    [Fact]
    public void C06_parallel_not_emitted_when_false()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"t"}],"tool_choice":{"type":"auto","disable_parallel_tool_use":false}""");
        body.ContainsKey("parallel_tool_calls").ShouldBeFalse();
    }

    [Fact]
    public void C09_unknown_string() => Reject("""{"model":"m","max_tokens":1,"tools":[{"name":"t"}],"tool_choice":"foo","messages":[{"role":"user","content":"x"}]}""", "tool_choice: Input should be 'auto', 'any', 'tool' or 'none'");

    [Fact]
    public void C10_unknown_type() => Reject("""{"model":"m","max_tokens":1,"tools":[{"name":"t"}],"tool_choice":{"type":"weird"},"messages":[{"role":"user","content":"x"}]}""", "tool_choice.type: Input should be 'auto', 'any', 'tool' or 'none'");

    [Fact]
    public void C11_missing_name() => Reject("""{"model":"m","max_tokens":1,"tools":[{"name":"t"}],"tool_choice":{"type":"tool"},"messages":[{"role":"user","content":"x"}]}""", "tool_choice.name: Field required");

    [Fact]
    public void C12_absent_tool() => Reject("""{"model":"m","max_tokens":1,"tools":[{"name":"t"}],"tool_choice":{"type":"tool","name":"nope"},"messages":[{"role":"user","content":"x"}]}""", "tool_choice.name: no tool named \"nope\" in tools");

    [Fact]
    public void C14_any_without_tools() => Reject("""{"model":"m","max_tokens":1,"tool_choice":{"type":"any"},"messages":[{"role":"user","content":"x"}]}""", "tool_choice: \"any\" requires at least one tool");

    [Fact]
    public void C15_auto_without_tools_omitted()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tool_choice":{"type":"auto"}""");
        body.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Fact]
    public void C16_none_with_tools_no_parallel()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"t"}],"tool_choice":{"type":"none","disable_parallel_tool_use":true}""");
        Compact(body["tool_choice"]).ShouldBe("\"none\"");
        body.ContainsKey("parallel_tool_calls").ShouldBeFalse();
    }

    [Fact]
    public void C17_non_object_tool_choice() => Reject("""{"model":"m","max_tokens":1,"tool_choice":5,"messages":[{"role":"user","content":"x"}]}""", "tool_choice: Input should be a valid dictionary");

    // ---------------------------------------------------------------- 18.9 limits/sampling/streaming

    [Theory]
    [InlineData(4096, "max_tokens", "4096")]
    [InlineData(1, "max_tokens", "16")]
    [InlineData(15, "max_tokens", "16")]
    [InlineData(16, "max_tokens", "16")]
    [InlineData(17, "max_tokens", "17")]
    public void Q01_Q03_output_floor(int clientMaxTokens, string key, string expected)
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""");
        string raw = $"{{\"model\":\"m\",\"max_tokens\":{clientMaxTokens},\"messages\":[{{\"role\":\"user\",\"content\":\"x\"}}]}}";
        JsonObject mapped = AnthropicChatJson.TryParseObject(Map(raw).ChatBody)!;
        Compact(mapped[key]).ShouldBe(expected);
        _ = body;
    }

    [Fact]
    public void Q04_cap_applied_after_floor()
    {
        var options = new AnthropicChatBridgeOptions(MaxOutputTokens: 8192);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":32000,"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!;
        Compact(body["max_tokens"]).ShouldBe("8192");
    }

    [Fact]
    public void Q05_max_completion_tokens_field()
    {
        var options = new AnthropicChatBridgeOptions(MaxTokensField: AnthropicChatMaxTokensField.MaxCompletionTokens);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1000,"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!;
        Compact(body["max_completion_tokens"]).ShouldBe("1000");
        body.ContainsKey("max_tokens").ShouldBeFalse();
    }

    [Fact]
    public void Q06_temperature_top_p()
    {
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"temperature":0.7,"top_p":0.9,"messages":[{"role":"user","content":"x"}]}""").ChatBody)!;
        Compact(body["temperature"]).ShouldBe("0.7");
        Compact(body["top_p"]).ShouldBe("0.9");
    }

    [Fact]
    public void Q07_Q08_stop_sequences()
    {
        JsonObject with = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"stop_sequences":["a","b"],"messages":[{"role":"user","content":"x"}]}""").ChatBody)!;
        Compact(with["stop"]).ShouldBe("[\"a\",\"b\"]");

        JsonObject empty = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"stop_sequences":[],"messages":[{"role":"user","content":"x"}]}""").ChatBody)!;
        empty.ContainsKey("stop").ShouldBeFalse();
    }

    [Fact]
    public void X01_stream_options()
    {
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"stream":true,"messages":[{"role":"user","content":"x"}]}""").ChatBody)!;
        Compact(body["stream"]).ShouldBe("true");
        Compact(body["stream_options"]).ShouldBe("{\"include_usage\":true}");
    }

    [Fact]
    public void X02_no_stream_keys()
    {
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"stream":false,"messages":[{"role":"user","content":"x"}]}""").ChatBody)!;
        body.ContainsKey("stream").ShouldBeFalse();
        body.ContainsKey("stream_options").ShouldBeFalse();
    }

    [Fact]
    public void X03_context_fields()
    {
        AnthropicChatRequestResult result = Map("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"}]}""", inboundModel: "claude-sonnet-4-5");
        result.Context.InboundModel.ShouldBe("claude-sonnet-4-5");
        result.Context.Stream.ShouldBeFalse();
        result.Context.RequestTokenEstimate.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void X04_body_model_copied_context_inbound()
    {
        AnthropicChatRequestResult result = Map("""{"model":"gpt-upstream","max_tokens":1,"messages":[{"role":"user","content":"x"}]}""", inboundModel: "claude-sonnet-4-5");
        JsonObject body = AnthropicChatJson.TryParseObject(result.ChatBody)!;
        body["model"]!.GetValue<string>().ShouldBe("gpt-upstream");
        result.Context.InboundModel.ShouldBe("claude-sonnet-4-5");
    }

    // ---------------------------------------------------------------- 18.10 reasoning

    [Fact]
    public void K00_gate_off_by_default()
    {
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"enabled","budget_tokens":4000},"output_config":{"effort":"high"},"messages":[{"role":"user","content":"x"}]}""").ChatBody)!;
        body.ContainsKey("reasoning_effort").ShouldBeFalse();
        body.ContainsKey("thinking").ShouldBeFalse();
    }

    [Fact]
    public void K01_budget_to_effort()
    {
        var options = new AnthropicChatBridgeOptions(SendReasoningEffort: true);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"enabled","budget_tokens":4000},"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!;
        Compact(body["reasoning_effort"]).ShouldBe("\"low\"");
    }

    [Theory]
    [InlineData(8191, "low")]
    [InlineData(8192, "medium")]
    [InlineData(10000, "medium")]
    [InlineData(24575, "medium")]
    [InlineData(24576, "high")]
    [InlineData(31999, "high")]
    public void K02_budget_thresholds(int budget, string expected)
    {
        var options = new AnthropicChatBridgeOptions(SendReasoningEffort: true);
        JsonObject body = AnthropicChatJson.TryParseObject(Map($"{{\"model\":\"m\",\"max_tokens\":1,\"thinking\":{{\"type\":\"enabled\",\"budget_tokens\":{budget}}},\"messages\":[{{\"role\":\"user\",\"content\":\"x\"}}]}}", options).ChatBody)!;
        Compact(body["reasoning_effort"]).ShouldBe($"\"{expected}\"");
    }

    [Fact]
    public void K03_explicit_effort_wins()
    {
        var options = new AnthropicChatBridgeOptions(SendReasoningEffort: true);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"enabled","budget_tokens":31999},"output_config":{"effort":"medium"},"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!;
        Compact(body["reasoning_effort"]).ShouldBe("\"medium\"");
    }

    [Theory]
    [InlineData("max")]
    [InlineData("xhigh")]
    public void K04_collapse_to_high(string effort)
    {
        var options = new AnthropicChatBridgeOptions(SendReasoningEffort: true);
        JsonObject body = AnthropicChatJson.TryParseObject(Map($"{{\"model\":\"m\",\"max_tokens\":1,\"output_config\":{{\"effort\":\"{effort}\"}},\"messages\":[{{\"role\":\"user\",\"content\":\"x\"}}]}}", options).ChatBody)!;
        Compact(body["reasoning_effort"]).ShouldBe("\"high\"");
    }

    [Fact]
    public void K05_disabled_thinking_wins()
    {
        var options = new AnthropicChatBridgeOptions(SendReasoningEffort: true);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"disabled"},"output_config":{"effort":"high"},"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!;
        body.ContainsKey("reasoning_effort").ShouldBeFalse();
    }

    [Fact]
    public void K06_adaptive()
    {
        var options = new AnthropicChatBridgeOptions(SendReasoningEffort: true);
        AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"adaptive"},"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!
            .ContainsKey("reasoning_effort").ShouldBeFalse();

        Compact(AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"adaptive"},"output_config":{"effort":"high"},"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!["reasoning_effort"])
            .ShouldBe("\"high\"");
    }

    [Fact]
    public void K10_history_thinking_dropped()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"plan","signature":"S"},{"type":"text","text":"hi"}]}]""");
        Compact(body["messages"]![1]!["content"]).ShouldBe("\"hi\"");
    }

    [Fact]
    public void K11_reasoning_only_removed()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"plan"}]},{"role":"user","content":"y"}]""");
        body["messages"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void K13_deepseek_profile()
    {
        var options = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.DeepSeek, SendReasoningEffort: true);
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"t"},{"type":"tool_use","id":"c","name":"f","input":{}}]}]""",
            extra: """, "thinking":{"type":"enabled","budget_tokens":4096},"output_config":{"effort":"medium"}""", options: options);
        Compact(body["thinking"]).ShouldBe("{\"type\":\"enabled\"}");
        Compact(body["reasoning_effort"]).ShouldBe("\"high\"");
        Compact(body["messages"]![1]!["reasoning_content"]).ShouldBe("\"t\"");
    }

    [Fact]
    public void K15_deepseek_disabled()
    {
        var options = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.DeepSeek, SendReasoningEffort: true);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"disabled"},"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!;
        Compact(body["thinking"]).ShouldBe("{\"type\":\"disabled\"}");
        body.ContainsKey("reasoning_effort").ShouldBeFalse();
    }

    [Fact]
    public void K17_deepseek_reasoning_only_kept()
    {
        var options = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.DeepSeek);
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"t"}]}]""", options: options);
        JsonObject assistant = body["messages"]![1]!.AsObject();
        Compact(assistant["content"]).ShouldBe("\"\"");
        Compact(assistant["reasoning_content"]).ShouldBe("\"t\"");
    }

    [Fact]
    public void K18_minimax_profile()
    {
        var options = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.MiniMax, SendReasoningEffort: true);
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"t"}]}]""", options: options);
        Compact(body["reasoning_split"]).ShouldBe("true");
        Compact(body["messages"]![1]!["reasoning_details"]).ShouldBe("[{\"type\":\"reasoning.text\",\"index\":0,\"text\":\"t\"}]");
        body.ContainsKey("thinking").ShouldBeFalse();
        body.ContainsKey("reasoning_effort").ShouldBeFalse();
    }

    [Fact]
    public void K25_minimax_reasoning_split_regardless_of_gate()
    {
        var options = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.MiniMax);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!;
        Compact(body["reasoning_split"]).ShouldBe("true");
        body.ContainsKey("reasoning_effort").ShouldBeFalse();
    }

    [Fact]
    public void K20_zhipu_profile()
    {
        var options = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.Zhipu, SendReasoningEffort: true);
        JsonObject body = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"output_config":{"effort":"high"},"messages":[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"t"}]}]}""", options).ChatBody)!;
        Compact(body["thinking"]).ShouldBe("{\"type\":\"enabled\"}");
        Compact(body["reasoning_effort"]).ShouldBe("\"high\"");
        body["messages"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void K22_signature_never_forwarded()
    {
        var options = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.DeepSeek);
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"S"}]}]""", options: options);
        body["messages"]![1]!.ToJsonString().ShouldNotContain("signature");
    }
    // ---------------------------------------------------------------- remaining 18.x rows

    [Fact]
    public void T03_T04_tool_message_precedes_sibling_text()
    {
        JsonObject after = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"done"},{"type":"text","text":"reminder"}]}]""");
        after["messages"]![2]!["role"]!.GetValue<string>().ShouldBe("tool");
        Compact(after["messages"]![3]!["content"]).ShouldBe("\"reminder\"");

        JsonObject before = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"f","input":{}}]},{"role":"user","content":[{"type":"text","text":"reminder"},{"type":"tool_result","tool_use_id":"t1","content":"done"}]}]""");
        before["messages"]![2]!["role"]!.GetValue<string>().ShouldBe("tool");
        Compact(before["messages"]![3]!["content"]).ShouldBe("\"reminder\"");
    }

    [Fact]
    public void T06_T08_orphan_results_become_user_text()
    {
        JsonObject mixed = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"A","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"A","content":"x"},{"type":"tool_result","tool_use_id":"Z","content":"y"}]}]""");
        Compact(mixed["messages"]![3]!["content"]).ShouldBe("\"Tool result (tool_use_id: Z):\\ny\"");

        JsonObject duplicate = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"A","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"A","content":"one"},{"type":"tool_result","tool_use_id":"A","content":"two"}]}]""");
        mixed["messages"]!.AsArray().Count.ShouldBe(4);
        duplicate["messages"]!.AsArray().Count.ShouldBe(4);
        Compact(duplicate["messages"]![3]!["content"]).ShouldBe("\"Tool result (tool_use_id: A):\\ntwo\"");
    }

    [Fact]
    public void T07_unanswered_call_is_kept()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"A","name":"f","input":{}},{"type":"tool_use","id":"B","name":"g","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"A","content":"x"}]}]""");
        body["messages"]![1]!["tool_calls"]!.AsArray().Count.ShouldBe(2);
        body["messages"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public void T14_empty_text_result_is_empty_string()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"text","text":""}]}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"\"");
    }

    [Fact]
    public void T17_T18_hoisted_images_order()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"C1","name":"f","input":{}},{"type":"tool_use","id":"C2","name":"g","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"C2","content":[{"type":"image","source":{"type":"url","url":"https://2"}}]},{"type":"tool_result","tool_use_id":"C1","content":[{"type":"image","source":{"type":"url","url":"https://1"}}]},{"type":"text","text":"sibling"}]}]""");
        JsonArray messages = body["messages"]!.AsArray();
        Compact(messages[2]!["tool_call_id"]).ShouldBe("\"C1\"");
        Compact(messages[3]!["tool_call_id"]).ShouldBe("\"C2\"");
        Compact(messages[4]!["content"]![0]!["text"]).ShouldBe("\"Images returned by tool call C1:\"");
        Compact(messages[5]!["content"]![0]!["text"]).ShouldBe("\"Images returned by tool call C2:\"");
        Compact(messages[6]!["content"]).ShouldBe("\"sibling\"");
    }

    [Fact]
    public void T21_tool_reference_appended_to_text()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"text","text":"x"},{"type":"tool_reference","tool_name":"mcp__a__b"}]}]}]""");
        Compact(body["messages"]![2]!["content"]).ShouldBe("\"x\\n[{\\\"type\\\":\\\"tool_reference\\\",\\\"tool_name\\\":\\\"mcp__a__b\\\"}]\"");
    }

    [Fact]
    public void T22_T23_T24_validation_and_arguments()
    {
        Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","content":"x"}]}]}""", "messages.2.content.0.tool_use_id: Field required");
        Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","name":"f"}]}]}""", "messages.1.content.0.id: Field required");
        Reject("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","input":{}}]}]}""", "messages.1.content.0.name: Field required");

        JsonObject absent = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f"}]}]""");
        Compact(absent["messages"]![1]!["tool_calls"]![0]!["function"]!["arguments"]).ShouldBe("\"{}\"");
    }

    [Fact]
    public void T30_T31_T32_shape_rules()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":"next"}]""");
        body["messages"]![2]!.AsObject().Select(pair => pair.Key).ShouldBe(new[] { "role", "content" });

        JsonObject tool = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"x"}]}]""");
        tool["messages"]![2]!.AsObject().Select(pair => pair.Key).ShouldBe(new[] { "role", "tool_call_id", "content" });

        JsonObject document = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"f","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"document"}]}]}]""");
        Compact(document["messages"]![2]!["content"]).ShouldBe("\"[omitted: unsupported content block 'document']\"");
    }

    [Fact]
    public void L01_L04_L05_L06_L07_tool_objects()
    {
        JsonObject full = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"b","description":"d","input_schema":{"type":"object","properties":{"n":{"type":"number","default":1.0}}},"strict":true},{"name":"a"}]""");
        Compact(full["tools"]![0]!["function"]).ShouldBe("{\"name\":\"b\",\"parameters\":{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"number\",\"default\":1.0}}},\"description\":\"d\",\"strict\":true}");
        Compact(full["tools"]![1]!["function"]!["name"]).ShouldBe("\"a\"");

        MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[]""").ContainsKey("tools").ShouldBeFalse();
        JsonObject searchOnly = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"type":"tool_search_tool_regex_20251119","name":"s"}]""");
        searchOnly.ContainsKey("tools").ShouldBeFalse();
        searchOnly.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Fact]
    public void N03_N09_N11_N12_name_map()
    {
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""", extra: """, "tools":[{"name":"__x__"},{"name":"mcp__srv__tool"},{"name":"my tool.v2"},{"name":"my_tool_v2"}]""");
        body["tools"]![0]!["function"]!["name"]!.GetValue<string>().ShouldBe("__x__");
        body["tools"]![1]!["function"]!["name"]!.GetValue<string>().ShouldBe("mcp__srv__tool");
        body["tools"]![2]!["function"]!["name"]!.GetValue<string>().ShouldBe("my_tool_v2");
        body["tools"]![3]!["function"]!["name"]!.GetValue<string>().ShouldBe("my_tool_v2_c7ba60b6");
    }

    [Fact]
    public void I02_I08_image_parts()
    {
        JsonObject only = MapMessages("""[{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://x"}}]}]""");
        only["messages"]![0]!["content"]!.AsArray().Count.ShouldBe(1);

        JsonObject mixed = MapMessages("""[{"role":"user","content":[{"type":"text","text":"A"},{"type":"image","source":{"type":"url","url":"https://x"}},{"type":"text","text":"B"}]}]""");
        JsonArray content = mixed["messages"]![0]!["content"]!.AsArray();
        content.Count.ShouldBe(3);
        Compact(content[0]!["text"]).ShouldBe("\"A\"");
        Compact(content[2]!["text"]).ShouldBe("\"B\"");
    }

    [Fact]
    public void K07_K09_K12_reasoning_gate()
    {
        var options = new AnthropicChatBridgeOptions(SendReasoningEffort: true);
        AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!
            .ContainsKey("reasoning_effort").ShouldBeFalse();

        Compact(AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"enabled","budget_tokens":10000},"output_config":{"effort":"ultra"},"messages":[{"role":"user","content":"x"}]}""", options).ChatBody)!["reasoning_effort"])
            .ShouldBe("\"medium\"");

        JsonObject redacted = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"redacted_thinking","data":"z"},{"type":"text","text":"hi"}]}]""");
        Compact(redacted["messages"]![1]!["content"]).ShouldBe("\"hi\"");
    }

    [Fact]
    public void K14_K16_K19_K24_K26_profiles()
    {
        var deepseek = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.DeepSeek, SendReasoningEffort: true);
        Compact(AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"output_config":{"effort":"max"},"messages":[{"role":"user","content":"x"}]}""", deepseek).ChatBody)!["reasoning_effort"])
            .ShouldBe("\"max\"");

        JsonObject adaptive = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"thinking":{"type":"adaptive"},"messages":[{"role":"user","content":"x"}]}""", deepseek).ChatBody)!;
        adaptive.ContainsKey("thinking").ShouldBeFalse();

        var mimo = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.Mimo, SendReasoningEffort: true);
        JsonObject mimoBody = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"t"}]}]""", extra: """, "thinking":{"type":"enabled","budget_tokens":4000}""", options: mimo);
        Compact(mimoBody["thinking"]).ShouldBe("{\"type\":\"enabled\"}");
        Compact(mimoBody["messages"]![1]!["reasoning_content"]).ShouldBe("\"t\"");
        mimoBody.ContainsKey("reasoning_effort").ShouldBeFalse();

        var deepseekDefault = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.DeepSeek);
        JsonObject defaultBody = MapMessages("""[{"role":"user","content":"x"},{"role":"assistant","content":[{"type":"thinking","thinking":"t"},{"type":"tool_use","id":"c","name":"f","input":{}}]}]""", options: deepseekDefault);
        defaultBody.ContainsKey("thinking").ShouldBeFalse();
        Compact(defaultBody["messages"]![1]!["reasoning_content"]).ShouldBe("\"t\"");

        var zhipuDefault = new AnthropicChatBridgeOptions(CompatProfile: AnthropicChatCompatProfile.Zhipu);
        JsonObject zhipuBody = AnthropicChatJson.TryParseObject(Map("""{"model":"m","max_tokens":1,"output_config":{"effort":"high"},"messages":[{"role":"user","content":"x"}]}""", zhipuDefault).ChatBody)!;
        zhipuBody.ContainsKey("thinking").ShouldBeFalse();
        zhipuBody.ContainsKey("reasoning_effort").ShouldBeFalse();
    }

    [Fact]
    public void S10_attribution_cache_control_is_not_forwarded()
    {
        var options = new AnthropicChatBridgeOptions(ForwardCacheControl: true);
        JsonObject body = MapMessages("""[{"role":"user","content":"x"}]""",
            extra: """, "system":[{"type":"text","text":"x-anthropic-billing-header: v","cache_control":{"type":"ephemeral"}},{"type":"text","text":"Be brief."}]""",
            options: options);
        JsonObject system = body["messages"]![0]!.AsObject();
        Compact(system["content"]).ShouldBe("\"Be brief.\"");
        system.ContainsKey("cache_control").ShouldBeFalse();

        JsonObject withBlockCache = MapMessages("""[{"role":"user","content":"x"}]""",
            extra: """, "system":[{"type":"text","text":"x-anthropic-billing-header: v"},{"type":"text","text":"Be brief.","cache_control":{"type":"ephemeral"}}]""",
            options: options);
        Compact(withBlockCache["messages"]![0]!["cache_control"]).ShouldBe("{\"type\":\"ephemeral\"}");
    }

}