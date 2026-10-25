using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Maps a Chat Completions reply to a Messages message (HLD 016 spec 02). Shared with the stream
/// translator for ids, names, arguments, text/reasoning handling, stop reason and usage.
/// </summary>
internal sealed class AnthropicChatResponseMapper(ILogger<AnthropicChatResponseMapper> logger)
{
    public static readonly string[] LengthFinishReasons =
    [
        "length", "max_tokens", "max_output_tokens", "max_completion_tokens", "model_context_window_exceeded"
    ];

    public static bool IsLengthFinishReason(string? finishReason) =>
        finishReason is not null && LengthFinishReasons.Contains(finishReason.Trim().ToLowerInvariant());

    /// <summary>Maps a 2xx Chat Completions object to a Messages message (spec 02 sections 3-10).</summary>
    public JsonObject MapSuccess(JsonObject root, AnthropicChatBridgeContext context)
    {
        JsonObject? choice = SelectChoice(root["choices"]);
        JsonObject? message = choice?["message"] as JsonObject ?? choice?["delta"] as JsonObject;
        if (message is null && AnthropicChatJson.String(choice?["text"]) is { } choiceText)
        {
            message = new JsonObject { ["content"] = choiceText };
        }

        string? finishReason = AnthropicChatJson.String(choice?["finish_reason"]);

        (string contentText, string refusalText) = ExtractText(message);
        string finalText = CombineRefusal(contentText, refusalText);

        string? markupReasoning = null;
        if (context.Options.ThinkMarkupExtraction)
        {
            (finalText, markupReasoning) = ExtractThinkMarkup(finalText);
        }

        string? reasoningText = ExtractReasoning(message, markupReasoning);

        var toolCalls = ExtractToolCalls(message, finishReason, context, finalText.Length > 0 || reasoningText is not null);

        // Spec 02 section 2 step 7: a choice-level error with no usable content is an in-band error, never
        // an empty 200 message. A choice-level error alongside any piece is ignored.
        if (toolCalls.Count == 0 && finalText.Length == 0 && reasoningText is null &&
            choice?["error"] is JsonObject choiceError)
        {
            throw new AnthropicChatChoiceErrorException(choiceError);
        }

        var content = new JsonArray();
        if (reasoningText is not null)
        {
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "thinking",
                ["thinking"] = reasoningText,
                ["signature"] = string.Empty
            });
        }

        if (finalText.Length > 0)
        {
            content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = finalText });
        }

        foreach (ToolCall call in toolCalls)
        {
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["input"] = call.Input
            });
        }

        string stopReason = toolCalls.Count > 0
            ? "tool_use"
            : IsLengthFinishReason(finishReason) ? "max_tokens" : "end_turn";

        JsonObject usage = BuildUsage(root, choice, context, content.Count > 0, reasoningText, finalText, toolCalls);

        return new JsonObject
        {
            ["id"] = AnthropicChatIds.NewMessageId(),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = context.InboundModel,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = usage
        };
    }

    private static JsonObject? SelectChoice(JsonNode? choices)
    {
        if (choices is not JsonArray array || array.Count == 0)
        {
            return null;
        }

        foreach (JsonNode? node in array)
        {
            if (node is JsonObject choice &&
                AnthropicChatJson.Number(choice["index"]) is double index && index == 0)
            {
                return choice;
            }
        }

        return array[0] as JsonObject;
    }

    private static (string Content, string Refusal) ExtractText(JsonObject? message)
    {
        if (message is null)
        {
            return (string.Empty, string.Empty);
        }

        var content = new System.Text.StringBuilder();
        var refusal = new System.Text.StringBuilder();

        if (AnthropicChatJson.String(message["refusal"]) is { } refusalString)
        {
            refusal.Append(refusalString);
        }

        switch (message["content"])
        {
            case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                content.Append(value.GetValue<string>());
                break;

            case JsonArray parts:
                foreach (JsonNode? part in parts)
                {
                    if (AnthropicChatJson.String(part) is { } stringPart)
                    {
                        content.Append(stringPart);
                        continue;
                    }

                    if (part is not JsonObject partObject)
                    {
                        continue;
                    }

                    string? type = AnthropicChatJson.String(partObject["type"]);
                    if (string.Equals(type, "refusal", StringComparison.Ordinal))
                    {
                        refusal.Append(AnthropicChatJson.String(partObject["refusal"])
                            ?? AnthropicChatJson.String(partObject["text"])
                            ?? string.Empty);
                    }
                    else if (AnthropicChatJson.String(partObject["text"]) is { } text)
                    {
                        content.Append(text);
                    }
                }
                break;
        }

        return (content.ToString(), refusal.ToString());
    }

    private static string CombineRefusal(string content, string refusal)
    {
        if (content.Length == 0)
        {
            return refusal;
        }

        return refusal.Length == 0 ? content : content + "\n" + refusal;
    }

    private static (string Text, string? Reasoning) ExtractThinkMarkup(string text)
    {
        foreach (string tag in (string[])["think", "thinking", "reasoning"])
        {
            string open = "<" + tag + ">";
            if (!text.StartsWith(open, StringComparison.Ordinal))
            {
                continue;
            }

            string close = "</" + tag + ">";
            int closeIndex = text.IndexOf(close, open.Length, StringComparison.Ordinal);
            return closeIndex >= 0
                ? (text[(closeIndex + close.Length)..], text[open.Length..closeIndex])
                : (string.Empty, text[open.Length..]);
        }

        return (text, null);
    }

    private static string? ExtractReasoning(JsonObject? message, string? markupReasoning)
    {
        if (message is null)
        {
            return markupReasoning;
        }

        var pieces = new List<string>();
        if (message["reasoning_details"] is JsonArray details)
        {
            foreach (JsonNode? detail in details)
            {
                if (AnthropicChatJson.String(detail) is { } stringPiece)
                {
                    if (!AnthropicChatJson.IsWhiteSpaceOnly(stringPiece))
                    {
                        pieces.Add(stringPiece);
                    }
                    continue;
                }

                if (detail is not JsonObject entry)
                {
                    continue;
                }

                if (AnthropicChatJson.String(entry["type"]) is "reasoning.encrypted" or "redacted_thinking")
                {
                    continue;
                }

                foreach (string member in (string[])["text", "thinking", "reasoning", "summary"])
                {
                    if (AnthropicChatJson.String(entry[member]) is { } piece &&
                        !AnthropicChatJson.IsWhiteSpaceOnly(piece))
                    {
                        pieces.Add(piece);
                        break;
                    }
                }
            }
        }

        string? detailsText = pieces.Count > 0 ? string.Join('\n', pieces) : null;

        string? plainText = null;
        foreach (string field in (string[])["reasoning_content", "reasoning", "thinking"])
        {
            if (AnthropicChatJson.String(message[field]) is { } value &&
                !AnthropicChatJson.IsWhiteSpaceOnly(value))
            {
                plainText = value;
                break;
            }
        }

        if (markupReasoning is not null)
        {
            plainText = plainText is null ? markupReasoning : markupReasoning + "\n" + plainText;
        }

        return MergeReasoning(detailsText, plainText, pieces);
    }

    private static string? MergeReasoning(string? detailsText, string? plainText, List<string> pieces)
    {
        if (detailsText is null)
        {
            return plainText;
        }

        if (plainText is null)
        {
            return detailsText;
        }

        string trimmedPlain = plainText.Trim();
        if (string.Equals(trimmedPlain, detailsText.Trim(), StringComparison.Ordinal) ||
            pieces.Any(piece => string.Equals(trimmedPlain, piece.Trim(), StringComparison.Ordinal)))
        {
            return detailsText;
        }

        return plainText + "\n" + detailsText;
    }

    private sealed record ToolCall(string Id, string Name, JsonObject Input);

    private List<ToolCall> ExtractToolCalls(
        JsonObject? message,
        string? finishReason,
        AnthropicChatBridgeContext context,
        bool hasTextOrReasoning)
    {
        var calls = new List<ToolCall>();
        if (message is null)
        {
            return calls;
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        bool hadNamelessCall = false;

        if (message["tool_calls"] is JsonArray { Count: > 0 } toolCalls)
        {
            foreach (JsonNode? node in toolCalls)
            {
                if (node is not JsonObject call)
                {
                    continue;
                }

                AddCall(call, null);
            }
        }
        else if (message["function_call"] is JsonObject legacyCall)
        {
            AddCall(legacyCall, legacy: true);
        }

        // A nameless sole call must fail the message (spec 02 section 7.2).
        if (hadNamelessCall && calls.Count == 0 && !hasTextOrReasoning)
        {
            throw new AnthropicChatFailureException(new AnthropicChatFailure(
                502, "api_error", "Upstream returned a tool call without a name."));
        }

        return calls;

        void AddCall(JsonObject call, bool? legacy)
        {
            JsonObject? function = call["function"] as JsonObject;
            string? name = AnthropicChatJson.String(function?["name"]) ?? AnthropicChatJson.String(call["name"]);
            if (string.IsNullOrEmpty(name))
            {
                hadNamelessCall = true;
                logger.LogWarning("Dropped an upstream tool call without a usable name");
                return;
            }

            string? upstreamId = legacy == true ? null : AnthropicChatJson.String(call["id"]);
            string id;
            if (upstreamId is not null &&
                AnthropicChatToolIdentity.IsLegalId(upstreamId) &&
                usedIds.Add(upstreamId))
            {
                id = upstreamId;
            }
            else
            {
                id = AnthropicChatIds.NewToolId(usedIds);
            }

            JsonNode? argumentsNode = FirstPresent(call, function);
            if (!AnthropicChatToolArguments.TryMapSource(argumentsNode, out string compact))
            {
                if (IsLengthFinishReason(finishReason))
                {
                    logger.LogWarning("Dropped tool call '{Name}' whose arguments were cut off by the length limit", name);
                    return;
                }

                throw new AnthropicChatFailureException(new AnthropicChatFailure(
                    502, "api_error", $"Upstream returned invalid arguments for tool call \"{name}\"."));
            }

            JsonObject input = AnthropicChatJson.TryParseObject(compact)
                ?? throw new AnthropicChatFailureException(new AnthropicChatFailure(
                    502, "api_error", $"Upstream returned invalid arguments for tool call \"{name}\"."));

            calls.Add(new ToolCall(id, context.ToolIdentity.OriginalFor(name), input));
        }

    }

    private static JsonNode? FirstPresent(JsonObject call, JsonObject? function)
    {
        if (function is not null && function.TryGetPropertyValue("arguments", out JsonNode? functionArguments) &&
            functionArguments is not null)
        {
            return functionArguments;
        }

        if (call.TryGetPropertyValue("arguments", out JsonNode? arguments) && arguments is not null)
        {
            return arguments;
        }

        if (call.TryGetPropertyValue("input", out JsonNode? input) && input is not null)
        {
            return input;
        }

        return null;
    }

    private JsonObject BuildUsage(
        JsonObject root,
        JsonObject? choice,
        AnthropicChatBridgeContext context,
        bool hasOutput,
        string? reasoningText,
        string text,
        List<ToolCall> toolCalls)
    {
        AnthropicChatUsageReader.Counts counts;
        JsonObject? usageObject = root["usage"] as JsonObject ?? choice?["usage"] as JsonObject;
        counts = usageObject is null
            ? new AnthropicChatUsageReader.Counts(null, null, null, null, null)
            : AnthropicChatUsageReader.Read(usageObject);

        double prompt = counts.Prompt ?? context.RequestTokenEstimate;

        double completion;
        if (counts.Completion is double reported && (reported > 0 || !hasOutput))
        {
            completion = reported;
        }
        else if (counts.Total is double total && counts.Prompt is double promptCount)
        {
            completion = Math.Max(0, total - promptCount);
        }
        else
        {
            completion = EstimateOutput(reasoningText, text, toolCalls);
        }

        double cacheRead = counts.CacheRead ?? 0;
        double cacheWrite = counts.CacheWrite ?? 0;
        double inputTokens = Math.Max(0, prompt - cacheRead - cacheWrite);

        return new JsonObject
        {
            ["input_tokens"] = (long)inputTokens,
            ["cache_creation_input_tokens"] = (long)cacheWrite,
            ["cache_read_input_tokens"] = (long)cacheRead,
            ["output_tokens"] = (long)completion
        };
    }

    private static double EstimateOutput(string? reasoningText, string text, List<ToolCall> toolCalls)
    {
        var builder = new System.Text.StringBuilder();
        if (reasoningText is not null)
        {
            builder.Append(reasoningText);
        }

        builder.Append(text);
        foreach (ToolCall call in toolCalls)
        {
            builder.Append(call.Name).Append(AnthropicChatJson.Compact(call.Input));
        }

        return AnthropicChatTokenEstimator.OutputEstimate(builder.ToString());
    }
}
