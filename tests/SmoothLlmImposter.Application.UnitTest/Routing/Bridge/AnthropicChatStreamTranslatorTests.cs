using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmoothLlmImposter.Application.Features.Routing.Bridge;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Bridge;

public class AnthropicChatStreamTranslatorTests
{
    private sealed record Frame(string Event, JsonObject Data);

    private static AnthropicChatCall Call(int estimate = 9, params string[] toolNames)
    {
        var identity = new AnthropicChatToolIdentity();
        foreach (string name in toolNames)
        {
            identity.RegisterName(name);
        }

        return new AnthropicChatCall(new AnthropicChatRequestResult(
            "{}",
            new AnthropicChatBridgeContext("M", true, identity, estimate, new AnthropicChatBridgeOptions())));
    }

    private static async Task<List<Frame>> Translate(string[] lines, AnthropicChatCall? call = null)
    {
        var translator = new AnthropicChatStreamTranslator(NullLogger<AnthropicChatStreamTranslator>.Instance);
        var frames = new List<Frame>();
        await foreach (string raw in translator.TranslateAsync(ToLines(lines), (call ?? Call()).Context, TimeProvider.System, CancellationToken.None))
        {
            frames.Add(Parse(raw));
        }

        return frames;
    }

    private static async IAsyncEnumerable<string> ToLines(string[] lines)
    {
        foreach (string line in lines)
        {
            yield return line;
        }

        await Task.CompletedTask;
    }

    private static Frame Parse(string raw)
    {
        string[] parts = raw.Split('\n');
        string eventName = parts[0]["event: ".Length..];
        JsonObject data = AnthropicChatJson.TryParseObject(parts[1]["data: ".Length..])!;
        return new Frame(eventName, data);
    }

    private static string[] Chunk(string json) => ["data: " + json, ""];

    private static string[] Chunks(params string[] json) => json.SelectMany(Chunk).ToArray();

    private static string Compact(JsonNode? node) => AnthropicChatJson.Compact(node);

    private static string TextChunk(string text) =>
        $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":{JsonValue.Create(text)!.ToJsonString()}}}}}]}}";

    private static string ReasoningChunk(string text) =>
        $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"reasoning_content\":{JsonValue.Create(text)!.ToJsonString()}}}}}]}}";

    private static string FinishChunk(string reason) =>
        $"{{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{reason}\"}}]}}";

    private static string UsageChunk(int prompt, int completion) =>
        $"{{\"choices\":[],\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion}}}}}";

    private const string RoleChunk = """{"choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""";

    private static void AssertEvents(List<Frame> frames, params string[] expected) =>
        frames.Select(f => f.Event).ShouldBe(expected);

    // ---------------------------------------------------------------- 10.2 lifecycle

    [Fact]
    public async Task L01_basic_stream()
    {
        List<Frame> frames = await Translate(Chunks(RoleChunk, TextChunk("Hi"), TextChunk(" yo"), FinishChunk("stop"), UsageChunk(10, 2)));
        AssertEvents(frames, "message_start", "ping", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop");
        Compact(frames[0].Data["message"]!["usage"]).ShouldBe("{\"input_tokens\":9,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}");
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"Hi\"}");
        Compact(frames[6].Data["usage"]).ShouldBe("{\"input_tokens\":10,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":2}");
        Compact(frames[6].Data["delta"]!["stop_reason"]).ShouldBe("\"end_turn\"");
    }

    [Fact]
    public async Task L02_text_and_finish_same_chunk()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            TextChunk("a"),
            """{"choices":[{"index":0,"delta":{"content":"b"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""));
        Compact(frames[4].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"b\"}");
        Compact(frames[5].Data).ShouldBe("{\"type\":\"content_block_stop\",\"index\":0}");
        Compact(frames[6].Data["delta"]!["stop_reason"]).ShouldBe("\"end_turn\"");
    }

    [Fact]
    public async Task L05_finish_then_done_without_usage_estimates()
    {
        List<Frame> frames = await Translate(Chunks(RoleChunk, TextChunk("Hi"), FinishChunk("stop"), "[DONE]"));
        AssertEvents(frames, "message_start", "ping", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop");
        Compact(frames[5].Data["usage"]!["input_tokens"]).ShouldBe("9");
        frames[5].Data["usage"]!["output_tokens"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task L07_done_without_finish_is_clean()
    {
        List<Frame> frames = await Translate(Chunks(RoleChunk, TextChunk("Hi"), "[DONE]"));
        AssertEvents(frames, "message_start", "ping", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop");
        Compact(frames[5].Data["delta"]!["stop_reason"]).ShouldBe("\"end_turn\"");
    }

    [Fact]
    public async Task L08_eof_without_finish_is_error()
    {
        List<Frame> frames = await Translate(Chunks(RoleChunk, TextChunk("Hi")));
        frames[^1].Event.ShouldBe("error");
        Compact(frames[^1].Data["error"]!["message"]).ShouldBe("\"Upstream stream ended before completion\"");
        frames.ShouldNotContain(f => f.Event == "message_delta");
    }

    [Fact]
    public async Task L09_eof_only_writes_only_error()
    {
        List<Frame> frames = await Translate([]);
        frames.Count.ShouldBe(1);
        frames[0].Event.ShouldBe("error");
    }

    [Fact]
    public async Task L10_done_only_is_empty_message()
    {
        List<Frame> frames = await Translate(Chunks("[DONE]"));
        AssertEvents(frames, "message_start", "ping", "message_delta", "message_stop");
        Compact(frames[2].Data["delta"]!["stop_reason"]).ShouldBe("\"end_turn\"");
        frames[2].Data["usage"]!["output_tokens"]!.GetValue<long>().ShouldBe(1);
    }

    [Fact]
    public async Task L11_finish_usage_no_blocks()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":0}}"""));
        AssertEvents(frames, "message_start", "ping", "message_delta", "message_stop");
        Compact(frames[2].Data["usage"]).ShouldBe("{\"input_tokens\":7,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":0}");
    }

    [Fact]
    public async Task L14_placeholder_usage_ignored()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            TextChunk("a"),
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{}}""",
            "[DONE]"));
        Compact(frames[^2].Data["usage"]!["input_tokens"]).ShouldBe("9");
        frames[^2].Data["usage"]!["output_tokens"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task L18_tool_call_burst()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":"{}"}}]}}]}""",
            FinishChunk("tool_calls"),
            FinishChunk("stop"),
            UsageChunk(2, 1)),
            Call(toolNames: ["f"]));
        AssertEvents(frames, "message_start", "ping", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop");
        Compact(frames[2].Data["content_block"]).ShouldBe("{\"type\":\"tool_use\",\"id\":\"c1\",\"name\":\"f\",\"input\":{}}");
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}");
        Compact(frames[5].Data["delta"]!["stop_reason"]).ShouldBe("\"tool_use\"");
    }

    // ---------------------------------------------------------------- 10.3 text

    [Fact]
    public async Task T01_whitespace_preserved()
    {
        List<Frame> frames = await Translate(Chunks(RoleChunk, TextChunk("\n"), "[DONE]"));
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"\\n\"}");
    }

    [Fact]
    public async Task T02_content_array_concatenates()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}}]}""",
            "[DONE]"));
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"ab\"}");
        frames.Count(f => f.Event == "content_block_delta").ShouldBe(1);
    }

    [Fact]
    public async Task T05_refusal_newline_piece()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"content":"x","refusal":"y"}}]}""",
            "[DONE]"));
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"x\"}");
        Compact(frames[4].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"\\n\"}");
        Compact(frames[5].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"y\"}");
    }

    [Fact]
    public async Task T07_message_instead_of_delta()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"message":{"content":"hi"}}]}""",
            "[DONE]"));
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"hi\"}");
    }

    [Fact]
    public async Task T08_index_selection()
    {
        List<Frame> frames = await Translate(Chunks(
            """{"choices":[{"index":1,"delta":{"content":"no"}},{"index":0,"delta":{"content":"x"}}]}""",
            "[DONE]"));
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"text_delta\",\"text\":\"x\"}");
    }

    // ---------------------------------------------------------------- 10.4 reasoning

    [Fact]
    public async Task K01_reasoning_then_text()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            ReasoningChunk("Hm"),
            ReasoningChunk(" ok"),
            TextChunk("Ans"),
            FinishChunk("stop"),
            UsageChunk(3, 4)));
        AssertEvents(frames,
            "message_start", "ping",
            "content_block_start", "content_block_delta", "content_block_delta",
            "content_block_stop", "content_block_start", "content_block_delta",
            "content_block_stop", "message_delta", "message_stop");
        Compact(frames[2].Data["content_block"]).ShouldBe("{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}");
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"thinking_delta\",\"thinking\":\"Hm\"}");
    }

    [Fact]
    public async Task K04_reasoning_details_order()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"reasoning_details":[{"type":"reasoning.summary","summary":"S"},{"type":"reasoning.text","text":"T"}]}}]}""",
            "[DONE]"));
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"thinking_delta\",\"thinking\":\"S\"}");
        Compact(frames[4].Data["delta"]).ShouldBe("{\"type\":\"thinking_delta\",\"thinking\":\"T\"}");
    }

    [Fact]
    public async Task K06_encrypted_only_no_block()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"reasoning_details":[{"type":"reasoning.encrypted","data":"abc"}]}}]}""",
            "[DONE]"));
        frames.ShouldNotContain(f => f.Event == "content_block_start");
    }

    [Fact]
    public async Task K13_think_text_think_runs()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            ReasoningChunk("a"),
            TextChunk("b"),
            ReasoningChunk("c"),
            "[DONE]"));
        AssertEvents(frames,
            "message_start", "ping",
            "content_block_start", "content_block_delta",
            "content_block_stop", "content_block_start", "content_block_delta",
            "content_block_stop", "content_block_start", "content_block_delta",
            "content_block_stop", "message_delta", "message_stop");
        Compact(frames[8].Data["content_block"]!["type"]).ShouldBe("\"thinking\"");
    }

    // ---------------------------------------------------------------- 10.5 tool calls

    [Fact]
    public async Task C01_fragmented_arguments()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_weather","arguments":""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"ci"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"X\"}"}}]}}]}""",
            FinishChunk("tool_calls"),
            UsageChunk(20, 5)),
            Call(toolNames: ["get_weather"]));
        Frame burst = frames[2];
        Compact(burst.Data["content_block"]).ShouldBe("{\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"get_weather\",\"input\":{}}");
        Compact(frames[3].Data["delta"]!["partial_json"]).ShouldBe("\"{\\\"city\\\":\\\"X\\\"}\"");
        Compact(frames[5].Data["delta"]!["stop_reason"]).ShouldBe("\"tool_use\"");
    }

    [Fact]
    public async Task C03_whole_call_held_until_finish()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":"{\"a\":1}"}}]}}]}""",
            FinishChunk("stop"),
            UsageChunk(2, 1)),
            Call(toolNames: ["f"]));
        AssertEvents(frames, "message_start", "ping", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop");
        Compact(frames[3].Data["delta"]!["partial_json"]).ShouldBe("\"{\\\"a\\\":1}\"");
    }

    [Fact]
    public async Task C05_interleaved_hold_mode()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"f","arguments":""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"g","arguments":""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"a\""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"b\""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":":1}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":":2}"}}]}}]}""",
            FinishChunk("tool_calls")),
            Call(toolNames: ["f", "g"]));
        frames.Count(f => f.Event == "content_block_start").ShouldBe(2);
        Compact(frames[2].Data["content_block"]!["name"]).ShouldBe("\"f\"");
        Compact(frames[3].Data["delta"]!["partial_json"]).ShouldBe("\"{\\\"a\\\":1}\"");
        Compact(frames[5].Data["content_block"]!["name"]).ShouldBe("\"g\"");
    }

    [Fact]
    public async Task C09_nameless_sole_call_errors()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"arguments":"{}"}}]}}]}""",
            FinishChunk("tool_calls"),
            UsageChunk(1, 1)));
        frames[^1].Event.ShouldBe("error");
        Compact(frames[^1].Data["error"]!["message"]).ShouldBe("\"Upstream returned a tool call without a name\"");
    }

    [Fact]
    public async Task C10_nameless_with_text_dropped()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            TextChunk("hi"),
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"arguments":"{}"}}]}}]}""",
            FinishChunk("stop"),
            UsageChunk(2, 1)));
        frames[^2].Event.ShouldBe("message_delta");
        Compact(frames[^2].Data["delta"]!["stop_reason"]).ShouldBe("\"end_turn\"");
    }

    [Theory]
    [InlineData("{\"a\":\"x")]
    [InlineData("{\"a\":1,")]
    [InlineData("{\"a\":[1,2")]
    public async Task C25_C29_repair(string fragment)
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            "{"
            + "\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\","
            + "\"function\":{\"name\":\"f\",\"arguments\":" + JsonValue.Create(fragment)!.ToJsonString() + "}}]}}]}",
            FinishChunk("tool_calls")),
            Call(toolNames: ["f"]));
        frames[3].Event.ShouldBe("content_block_delta");
        Compact(frames[3].Data["delta"]!["partial_json"]).ShouldNotContain("error");
    }

    [Fact]
    public async Task C27_unrepairable_errors()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":"{\"a\":}"}}]}}]}""",
            FinishChunk("tool_calls")),
            Call(toolNames: ["f"]));
        frames[^1].Event.ShouldBe("error");
        Compact(frames[^1].Data["error"]!["message"]).ShouldBe("\"Upstream returned invalid arguments for tool call f\"");
    }

    [Fact]
    public async Task C31_length_cut_drops_call()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":"{\"path\":\"x"}}]}}]}""",
            FinishChunk("length"),
            UsageChunk(2, 1)),
            Call(toolNames: ["f"]));
        frames.ShouldNotContain(f => f.Event == "content_block_start");
        Compact(frames[^2].Data["delta"]!["stop_reason"]).ShouldBe("\"max_tokens\"");
    }

    [Fact]
    public async Task C33_earlier_call_survives_length()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"f","arguments":"{\"a\":1}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"g","arguments":"{\"b\":"}}]}}]}""",
            FinishChunk("length"),
            UsageChunk(2, 1)),
            Call(toolNames: ["f", "g"]));
        frames.Count(f => f.Event == "content_block_start").ShouldBe(1);
        Compact(frames[^2].Data["delta"]!["stop_reason"]).ShouldBe("\"tool_use\"");
    }

    [Fact]
    public async Task C41_too_many_calls()
    {
        var lines = new List<string>();
        lines.AddRange(Chunk(RoleChunk));
        for (int i = 0; i < 129; i++)
        {
            lines.AddRange(Chunk($"{{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{i},\"id\":\"c{i}\",\"function\":{{\"name\":\"f\",\"arguments\":\"{{}}\"}}}}]}}}}]}}"));
        }

        List<Frame> frames = await Translate(lines.ToArray(), Call(toolNames: ["f"]));
        frames[^1].Event.ShouldBe("error");
        Compact(frames[^1].Data["error"]!["message"]).ShouldBe("\"Upstream returned too many tool calls\"");
    }

    // ---------------------------------------------------------------- 10.6 stop_reason

    [Theory]
    [InlineData("stop", "end_turn")]
    [InlineData("length", "max_tokens")]
    [InlineData("content_filter", "end_turn")]
    [InlineData("MAX_TOKENS", "max_tokens")]
    [InlineData("foo", "end_turn")]
    [InlineData("function_call", "end_turn")]
    public async Task S01_S07_stop_reason(string finish, string expected)
    {
        List<Frame> frames = await Translate(Chunks(RoleChunk, TextChunk("x"), FinishChunk(finish), UsageChunk(1, 1)));
        Compact(frames[^2].Data["delta"]!["stop_reason"]).ShouldBe($"\"{expected}\"");
    }

    // ---------------------------------------------------------------- 10.7 usage

    [Fact]
    public async Task U01_cache_read_subtracted()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            TextChunk("x"),
            FinishChunk("stop"),
            """{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":60}}}"""));
        Compact(frames[^2].Data["usage"]).ShouldBe("{\"input_tokens\":40,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":60,\"output_tokens\":20}");
    }

    [Fact]
    public async Task U02_both_cache_counters_subtracted()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            TextChunk("x"),
            FinishChunk("stop"),
            """{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":60,"cache_write_tokens":10}}}"""));
        Compact(frames[^2].Data["usage"]).ShouldBe("{\"input_tokens\":30,\"cache_creation_input_tokens\":10,\"cache_read_input_tokens\":60,\"output_tokens\":20}");
    }

    [Fact]
    public async Task U11_zero_completion_with_content_estimates()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            TextChunk("hello"),
            FinishChunk("stop"),
            UsageChunk(10, 0)));
        frames[^2].Data["usage"]!["output_tokens"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(1);
    }

    // ---------------------------------------------------------------- 10.8 errors

    [Fact]
    public async Task H01_event_error_mid_stream()
    {
        List<Frame> frames = await Translate([
            "data: " + RoleChunk, "",
            "data: " + TextChunk("Par"), "",
            "event: error",
            "data: {\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}",
            ""]);
        frames[^1].Event.ShouldBe("error");
        Compact(frames[^1].Data["error"]!["type"]).ShouldBe("\"overloaded_error\"");
        Compact(frames[^1].Data["error"]!["message"]).ShouldBe("\"busy\"");
        frames.ShouldNotContain(f => f.Event == "message_delta");
    }

    [Fact]
    public async Task H02_bare_error_object()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"error":{"message":"rate limited","type":"rate_limit_error"}}"""));
        frames[^1].Event.ShouldBe("error");
        Compact(frames[^1].Data["error"]!["type"]).ShouldBe("\"rate_limit_error\"");
    }

    [Fact]
    public async Task H05_numeric_code()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"error":{"code":429,"message":"slow"}}"""));
        Compact(frames[^1].Data["error"]!["type"]).ShouldBe("\"rate_limit_error\"");
    }

    [Fact]
    public async Task H07_overflow_rewording()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"error":{"code":"context_length_exceeded","message":"maximum context length is 8192"}}"""));
        Compact(frames[^1].Data["error"]!["type"]).ShouldBe("\"invalid_request_error\"");
        frames[^1].Data["error"]!["message"]!.GetValue<string>().ShouldStartWith("prompt is too long");
    }

    [Fact]
    public async Task H11_null_error_is_ordinary_chunk()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"content":"x"}}],"error":null}""",
            "[DONE]"));
        frames.ShouldNotContain(f => f.Event == "error");
    }

    // ---------------------------------------------------------------- 10.9 SSE parsing

    [Fact]
    public async Task P04_multi_line_data_joined()
    {
        List<Frame> frames = await Translate([
            "data: {\"choices\":[{\"index\":0,",
            "data: \"delta\":{\"content\":\"x\"}}]}",
            "",
            "data: [DONE]",
            ""]);
        frames[3].Event.ShouldBe("content_block_delta");
    }

    [Fact]
    public async Task P05_P06_data_prefix_spacing()
    {
        List<Frame> a = await Translate(["data:" + RoleChunk, "", "data: [DONE]", ""]);
        a[0].Event.ShouldBe("message_start");
        List<Frame> b = await Translate(["data:  " + RoleChunk, "", "data: [DONE]", ""]);
        b[0].Event.ShouldBe("message_start");
    }

    [Fact]
    public async Task P07_P08_comments_and_unknown_fields_ignored()
    {
        List<Frame> frames = await Translate([": keep-alive", "", "id: 1", "retry: 2", "data: " + RoleChunk, "", "data: [DONE]", ""]);
        frames[0].Event.ShouldBe("message_start");
    }

    [Fact]
    public async Task P09_last_event_without_trailing_blank()
    {
        List<Frame> frames = await Translate(["data: " + RoleChunk, "", "data: [DONE]"]);
        frames[0].Event.ShouldBe("message_start");
    }

    [Fact]
    public async Task P14_invalid_json_ignored()
    {
        List<Frame> frames = await Translate(["data: junk", "", "data: [DONE]", ""]);
        frames[0].Event.ShouldBe("message_start");
    }

    // ---------------------------------------------------------------- timers

    [Fact]
    public async Task L21_idle_starts_message_then_pings()
    {
        var call = Call();
        await using var harness = new StreamHarness(call);
        await harness.Time.WaitForTimerAsync(due => due >= TimeSpan.FromSeconds(7));
        harness.Advance(TimeSpan.FromSeconds(8));
        List<string> first = await harness.WaitAsync(2);
        first[0].ShouldContain("event: message_start");
        first[1].ShouldContain("event: ping");

        harness.Advance(TimeSpan.FromSeconds(8));
        List<string> second = await harness.WaitAsync(3);
        second[2].ShouldContain("event: ping");
    }

    [Fact]
    public async Task L22_idle_ping_after_text()
    {
        var call = Call();
        await using var harness = new StreamHarness(call);
        harness.Write("data: " + RoleChunk, "", "data: " + TextChunk("a"), "");
        await harness.WaitAsync(4);
        await harness.Time.WaitForTimerAsync(due => due >= TimeSpan.FromSeconds(7));
        harness.Advance(TimeSpan.FromSeconds(8));
        List<string> frames = await harness.WaitAsync(5);
        frames[4].ShouldContain("event: ping");
    }

    [Fact]
    public async Task L04_finish_grace_finalizes()
    {
        var call = Call();
        await using var harness = new StreamHarness(call);
        harness.Write("data: " + RoleChunk, "", "data: " + TextChunk("a"), "");
        harness.Write("data: " + UsageChunk(5, 1), "");
        harness.Write("data: " + FinishChunk("stop"), "");
        await harness.WaitAsync(4);
        await harness.Time.WaitForTimerAsync(due => due > TimeSpan.Zero && due <= TimeSpan.FromSeconds(2.5));
        harness.Advance(TimeSpan.FromSeconds(2));
        List<string> frames = await harness.WaitAsync(7);
        frames[^1].ShouldContain("event: message_stop");
        frames[^2].ShouldContain("event: message_delta");
    }

    // ---------------------------------------------------------------- regression coverage (RL-010/RL-017/RL-002)

    [Fact]
    public async Task In_band_401_carries_upstream_credential_wording_without_status()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"error":{"type":"authentication_error","message":"bad key"}}"""));
        Compact(frames[^1].Data["error"]!["type"]).ShouldBe("\"authentication_error\"");
        Compact(frames[^1].Data["error"]!["message"])
            .ShouldBe("\"The upstream provider rejected the API key configured for this route: bad key\"");
    }

    [Fact]
    public async Task In_band_401_carries_http_status_when_numeric()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"error":{"status":401,"message":"bad key"}}"""));
        Compact(frames[^1].Data["error"]!["message"])
            .ShouldBe("\"The upstream provider rejected the API key configured for this route (HTTP 401): bad key\"");
    }

    [Fact]
    public async Task In_band_403_carries_permission_wording()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"error":{"type":"permission_error","message":"forbidden"}}"""));
        Compact(frames[^1].Data["error"]!["message"])
            .ShouldBe("\"The upstream provider denied access for the API key configured for this route: forbidden\"");
    }

    [Fact]
    public async Task Reasoning_details_member_precedence_is_reasoning_before_thinking()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"index":0,"delta":{"reasoning_details":[{"reasoning":"r","thinking":"t"}]}}]}""",
            "[DONE]"));
        Compact(frames[3].Data["delta"]).ShouldBe("{\"type\":\"thinking_delta\",\"thinking\":\"r\"}");
    }

    [Fact]
    public async Task Long_text_stream_backpressure_completes_with_every_delta()
    {
        const int chunks = 2000;
        var lines = new List<string>(chunks * 2 + 2);
        lines.AddRange(Chunk(RoleChunk));
        for (int i = 0; i < chunks; i++)
        {
            lines.AddRange(Chunk(TextChunk("x")));
        }

        lines.AddRange(Chunk("[DONE]"));
        List<Frame> frames = await Translate(lines.ToArray());
        frames.Count(f => f.Event == "content_block_delta").ShouldBe(chunks);
        frames[^2].Event.ShouldBe("message_delta");
        frames[^1].Event.ShouldBe("message_stop");
    }

    [Fact]
    public async Task Choice_level_overflow_uses_top_level_numeric_members()
    {
        List<Frame> frames = await Translate(Chunks(
            RoleChunk,
            """{"choices":[{"error":{"code":"context_length_exceeded","n_prompt_tokens":5000,"n_ctx":4096}}]}"""));
        frames[^1].Event.ShouldBe("error");
        Compact(frames[^1].Data["error"]!["type"]).ShouldBe("\"invalid_request_error\"");
        Compact(frames[^1].Data["error"]!["message"]).ShouldBe("\"prompt is too long: 5000 tokens > 4096 maximum\"");
    }

    // ---------------------------------------------------------------- harness

    private sealed class StreamHarness : IAsyncDisposable
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly List<string> _frames = [];
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public ManualTimeProvider Time { get; } = new();

        public StreamHarness(AnthropicChatCall call)
        {
            var translator = new AnthropicChatStreamTranslator(NullLogger<AnthropicChatStreamTranslator>.Instance);
            _run = Task.Run(async () =>
            {
                try
                {
                    await foreach (string frame in translator.TranslateAsync(
                        _lines.Reader.ReadAllAsync(_cts.Token), call.Context, Time, _cts.Token))
                    {
                        lock (_frames)
                        {
                            _frames.Add(frame);
                        }

                        _signal.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public void Write(params string[] lines)
        {
            foreach (string line in lines)
            {
                _lines.Writer.TryWrite(line);
            }
        }

        public void Advance(TimeSpan delta) => Time.Advance(delta);

        public async Task<List<string>> WaitAsync(int count)
        {
            int deadline = Environment.TickCount + 5000;
            while (true)
            {
                lock (_frames)
                {
                    if (_frames.Count >= count)
                    {
                        return _frames.ToList();
                    }
                }

                int remaining = deadline - Environment.TickCount;
                if (remaining <= 0)
                {
                    throw new TimeoutException($"Only {_frames.Count} frames; wanted {count}");
                }

                await _signal.WaitAsync(remaining);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _lines.Writer.TryComplete();
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, period);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + dueTime;
            }

            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += delta;
                due = _timers.Where(timer => timer.DueAt <= _now).ToList();
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }

        internal DateTimeOffset Now
        {
            get
            {
                lock (_gate)
                {
                    return _now;
                }
            }
        }

        public async Task WaitForTimerAsync(Func<TimeSpan, bool> predicate)
        {
            int deadline = Environment.TickCount + 5000;
            while (true)
            {
                lock (_gate)
                {
                    if (_timers.Any(timer => predicate(timer.DueAt - _now)))
                    {
                        return;
                    }
                }

                if (Environment.TickCount - deadline > 0)
                {
                    throw new TimeoutException("No matching timer was registered.");
                }

                await Task.Delay(5);
            }
        }

        internal void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        private readonly TimerCallback _callback = callback;
        private readonly object? _state = state;
        private readonly TimeSpan _period = period;

        public DateTimeOffset DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                DueAt = DateTimeOffset.MaxValue;
                owner.Remove(this);
                return true;
            }

            DueAt = owner.Now + dueTime;
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void Fire()
        {
            _callback(_state);
            if (_period == Timeout.InfiniteTimeSpan)
            {
                Dispose();
            }
            else
            {
                DueAt = owner.Now + _period;
            }
        }
    }
}
