using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>Exact Messages SSE frame shapes (HLD 016 spec 03 section 3.2).</summary>
internal static class AnthropicChatFrames
{
    public static string MessageStart(string id, string model, int inputEstimate) =>
        Frame("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = id,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = inputEstimate,
                    ["cache_creation_input_tokens"] = 0,
                    ["cache_read_input_tokens"] = 0,
                    ["output_tokens"] = 1
                }
            }
        });

    public static string Ping() => Frame("ping", new JsonObject { ["type"] = "ping" });

    public static string TextStart(int index) => Frame("content_block_start", new JsonObject
    {
        ["type"] = "content_block_start",
        ["index"] = index,
        ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = string.Empty }
    });

    public static string ThinkingStart(int index) => Frame("content_block_start", new JsonObject
    {
        ["type"] = "content_block_start",
        ["index"] = index,
        ["content_block"] = new JsonObject
        {
            ["type"] = "thinking",
            ["thinking"] = string.Empty,
            ["signature"] = string.Empty
        }
    });

    public static string ToolUseStart(int index, string id, string name) => Frame("content_block_start", new JsonObject
    {
        ["type"] = "content_block_start",
        ["index"] = index,
        ["content_block"] = new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = id,
            ["name"] = name,
            ["input"] = new JsonObject()
        }
    });

    public static string TextDelta(int index, string text) => Frame("content_block_delta", new JsonObject
    {
        ["type"] = "content_block_delta",
        ["index"] = index,
        ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
    });

    public static string ThinkingDelta(int index, string thinking) => Frame("content_block_delta", new JsonObject
    {
        ["type"] = "content_block_delta",
        ["index"] = index,
        ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking }
    });

    public static string ToolInputDelta(int index, string partialJson) => Frame("content_block_delta", new JsonObject
    {
        ["type"] = "content_block_delta",
        ["index"] = index,
        ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = partialJson }
    });

    public static string ContentBlockStop(int index) => Frame("content_block_stop", new JsonObject
    {
        ["type"] = "content_block_stop",
        ["index"] = index
    });

    public static string MessageDelta(string stopReason, long input, long cacheCreation, long cacheRead, long output) =>
        Frame("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["cache_creation_input_tokens"] = cacheCreation,
                ["cache_read_input_tokens"] = cacheRead,
                ["output_tokens"] = output
            }
        });

    public static string MessageStop() => Frame("message_stop", new JsonObject { ["type"] = "message_stop" });

    public static string Error(string type, string message) => Frame("error", new JsonObject
    {
        ["type"] = "error",
        ["error"] = new JsonObject { ["type"] = type, ["message"] = message }
    });

    public static string Frame(string eventName, JsonObject data) =>
        $"event: {eventName}\ndata: {AnthropicChatJson.Compact(data)}\n\n";
}
