using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public sealed class ChatToResponsesStreamTransformerTests
{
    private readonly ChatToResponsesStreamTransformer _transformer = new();

    [Fact]
    public async Task Streaming_chat_chunks_are_translated_to_ordered_responses_events()
    {
        IReadOnlyList<(string Event, JsonObject Data)> frames = await CollectFramesAsync(
            """data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
            "",
            """data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}""",
            "",
            """data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"content":"lo","reasoning_content":"thinking"},"finish_reason":null}]}""",
            "",
            """data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"q\""}}]},"finish_reason":null}]}""",
            "",
            """data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"x\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}""",
            "",
            "data: [DONE]",
            "");

        frames.Select(f => f.Event).ShouldBe([
            "response.created",
            "response.in_progress",
            "response.output_item.added",
            "response.content_part.added",
            "response.output_text.delta",
            "response.output_text.delta",
            "response.reasoning_summary_text.delta",
            "response.output_item.added",
            "response.function_call_arguments.delta",
            "response.function_call_arguments.delta",
            "response.output_text.done",
            "response.content_part.done",
            "response.output_item.done",
            "response.function_call_arguments.done",
            "response.output_item.done",
            "response.completed"
        ]);

        frames.Single(f => f.Event == "response.output_text.done").Data["text"]!.GetValue<string>().ShouldBe("Hello");
        frames.Single(f => f.Event == "response.function_call_arguments.done").Data["arguments"]!.GetValue<string>().ShouldBe("""{"q":"x"}""");

        JsonObject completed = frames.Last().Data["response"]!.AsObject();
        completed["id"]!.GetValue<string>().ShouldBe("resp_chatcmpl_1");
        completed["model"]!.GetValue<string>().ShouldBe("kimi");
        completed["usage"]!["input_tokens"]!.GetValue<int>().ShouldBe(4);
        completed["usage"]!["output_tokens"]!.GetValue<int>().ShouldBe(2);

        JsonArray output = completed["output"]!.AsArray();
        output.Count.ShouldBe(2);
        output[0]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>().ShouldBe("Hello");
        output[1]!["type"]!.GetValue<string>().ShouldBe("function_call");
        output[1]!["name"]!.GetValue<string>().ShouldBe("lookup");
        output[1]!["arguments"]!.GetValue<string>().ShouldBe("""{"q":"x"}""");
    }

    [Fact]
    public async Task Streaming_transform_yields_before_upstream_stream_completes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IAsyncEnumerator<string> enumerator = _transformer
            .TransformStreamingAsync(SlowLines(release.Task, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.ShouldStartWith("event: response.created");

        release.SetResult();
    }

    [Fact]
    public void Non_streaming_chat_completion_is_translated_to_response_object()
    {
        string result = _transformer.TransformNonStreaming("""
        {
          "id":"chatcmpl_2",
          "object":"chat.completion",
          "created":11,
          "model":"kimi",
          "choices":[{"index":0,"message":{"role":"assistant","content":"Hi","tool_calls":[{"id":"call_2","type":"function","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":"tool_calls"}],
          "usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}
        }
        """);

        JsonObject response = JsonNode.Parse(result)!.AsObject();
        response["object"]!.GetValue<string>().ShouldBe("response");
        response["id"]!.GetValue<string>().ShouldBe("resp_chatcmpl_2");
        response["usage"]!["total_tokens"]!.GetValue<int>().ShouldBe(4);

        JsonArray output = response["output"]!.AsArray();
        output[0]!["type"]!.GetValue<string>().ShouldBe("message");
        output[0]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>().ShouldBe("Hi");
        output[1]!["type"]!.GetValue<string>().ShouldBe("function_call");
        output[1]!["call_id"]!.GetValue<string>().ShouldBe("call_2");
    }

    private async Task<IReadOnlyList<(string Event, JsonObject Data)>> CollectFramesAsync(params string[] lines)
    {
        var frames = new List<(string Event, JsonObject Data)>();
        await foreach (string frame in _transformer.TransformStreamingAsync(Lines(lines), TestContext.Current.CancellationToken))
        {
            string[] parts = frame.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string eventName = parts.Single(p => p.StartsWith("event: ", StringComparison.Ordinal))[7..];
            string json = parts.Single(p => p.StartsWith("data: ", StringComparison.Ordinal))[6..];
            frames.Add((eventName, JsonNode.Parse(json)!.AsObject()));
        }

        return frames;
    }

    private static async IAsyncEnumerable<string> Lines(
        IEnumerable<string> lines,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> SlowLines(
        Task wait,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return """data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":10,"model":"kimi","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""";
        yield return "";
        await wait.WaitAsync(cancellationToken);
    }
}
