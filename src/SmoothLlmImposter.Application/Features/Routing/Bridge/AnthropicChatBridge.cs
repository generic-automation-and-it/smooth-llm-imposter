using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Orchestrates the Messages-to-Chat bridge: request mapping, reply mapping and local token counting.
/// It never forwards or retries; the Host owns those concerns (HLD 016 D17/D21).
/// </summary>
internal sealed class AnthropicChatBridge(
    AnthropicChatRequestMapper requestMapper,
    AnthropicChatResponseMapper responseMapper,
    AnthropicChatStreamTranslator streamTranslator,
    TimeProvider timeProvider,
    ILogger<AnthropicChatBridge> logger) : IAnthropicChatBridge
{
    public AnthropicChatCall MapRequest(string messagesBody, string inboundModel, AnthropicChatBridgeOptions options)
    {
        try
        {
            return new AnthropicChatCall(requestMapper.Map(messagesBody, inboundModel, options));
        }
        catch (AnthropicChatRequestException ex)
        {
            logger.LogDebug("Rejected bridged Messages request: {Reason}", ex.Message);
            throw;
        }
    }

    public AnthropicChatReply MapResponse(
        int upstreamStatus,
        string body,
        string? retryAfter,
        string? retryAfterMs,
        AnthropicChatCall call,
        string? configuredSecret)
    {
        try
        {
            if (upstreamStatus is < 200 or > 299)
            {
                AnthropicChatFailure failure = AnthropicChatErrorMapper.Map(upstreamStatus, body, configuredSecret);
                return FailureReply(failure, retryAfter, retryAfterMs);
            }

            JsonObject? root = AnthropicChatJson.TryParseObject(body);
            if (root is null)
            {
                return FailureReply(
                    new AnthropicChatFailure(502, "api_error", "Upstream returned a response that is not a Chat Completions object."),
                    retryAfter,
                    retryAfterMs);
            }

            if (IsNonEmptyError(root["error"]) && root["choices"] is not JsonArray)
            {
                AnthropicChatFailure failure = AnthropicChatErrorMapper.MapInBand(root, configuredSecret);
                return FailureReply(failure, retryAfter, retryAfterMs);
            }

            if (root["choices"] is not JsonArray)
            {
                return FailureReply(
                    new AnthropicChatFailure(502, "api_error", "Upstream returned a response that is not a Chat Completions object."),
                    retryAfter,
                    retryAfterMs);
            }

            JsonObject message = responseMapper.MapSuccess(root, call.Context);
            return new AnthropicChatReply(200, AnthropicChatJson.CompactObject(message));
        }
        catch (AnthropicChatChoiceErrorException ex)
        {
            AnthropicChatFailure failure = AnthropicChatErrorMapper.MapInBand(ex.Choice, configuredSecret);
            return FailureReply(failure, retryAfter, retryAfterMs);
        }
        catch (AnthropicChatFailureException ex)
        {
            return FailureReply(ex.Failure, retryAfter, retryAfterMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Messages bridge failed to map the upstream reply");
            return FailureReply(
                new AnthropicChatFailure(500, "api_error", "Internal error in the Messages bridge."),
                retryAfter,
                retryAfterMs);
        }
    }

    public IAsyncEnumerable<string> TranslateStreamAsync(
        IAsyncEnumerable<string> upstreamLines,
        AnthropicChatCall call,
        CancellationToken cancellationToken) =>
        streamTranslator.TranslateAsync(upstreamLines, call.Context, timeProvider, cancellationToken);

    public IEnumerable<string> ReplayAsStream(AnthropicChatReply reply, AnthropicChatCall call)
    {
        if (reply.StatusCode != 200)
        {
            yield return AnthropicChatFrames.Error(ErrorType(reply.Body), ErrorMessage(reply.Body));
            yield break;
        }

        JsonObject? message = AnthropicChatJson.TryParseObject(reply.Body);
        if (message is null || message["content"] is not JsonArray content)
        {
            yield return AnthropicChatFrames.Error("api_error", "Internal error in the Messages bridge.");
            yield break;
        }

        string id = AnthropicChatJson.String(message["id"]) ?? AnthropicChatIds.NewMessageId();
        yield return AnthropicChatFrames.MessageStart(id, call.Context.InboundModel, call.Context.RequestTokenEstimate);
        yield return AnthropicChatFrames.Ping();

        int index = 0;
        foreach (JsonNode? node in content)
        {
            if (node is not JsonObject block)
            {
                continue;
            }

            string? type = AnthropicChatJson.String(block["type"]);
            switch (type)
            {
                case "thinking":
                    yield return AnthropicChatFrames.ThinkingStart(index);
                    yield return AnthropicChatFrames.ThinkingDelta(index, AnthropicChatJson.String(block["thinking"]) ?? string.Empty);
                    yield return AnthropicChatFrames.ContentBlockStop(index);
                    break;

                case "text":
                    yield return AnthropicChatFrames.TextStart(index);
                    yield return AnthropicChatFrames.TextDelta(index, AnthropicChatJson.String(block["text"]) ?? string.Empty);
                    yield return AnthropicChatFrames.ContentBlockStop(index);
                    break;

                case "tool_use":
                    yield return AnthropicChatFrames.ToolUseStart(
                        index,
                        AnthropicChatJson.String(block["id"]) ?? AnthropicChatIds.NewMessageId(),
                        AnthropicChatJson.String(block["name"]) ?? string.Empty);
                    yield return AnthropicChatFrames.ToolInputDelta(
                        index,
                        AnthropicChatJson.Compact(block["input"] ?? new JsonObject()));
                    yield return AnthropicChatFrames.ContentBlockStop(index);
                    break;

                default:
                    continue;
            }

            index++;
        }

        JsonObject usage = message["usage"] as JsonObject ?? new JsonObject();
        yield return AnthropicChatFrames.MessageDelta(
            AnthropicChatJson.String(message["stop_reason"]) ?? "end_turn",
            (long)(AnthropicChatJson.Number(usage["input_tokens"]) ?? 0),
            (long)(AnthropicChatJson.Number(usage["cache_creation_input_tokens"]) ?? 0),
            (long)(AnthropicChatJson.Number(usage["cache_read_input_tokens"]) ?? 0),
            (long)(AnthropicChatJson.Number(usage["output_tokens"]) ?? 0));
        yield return AnthropicChatFrames.MessageStop();
    }

    public string CountTokens(string messagesBody, AnthropicChatBridgeOptions options)
    {
        JsonObject root = AnthropicChatJson.TryParseObject(messagesBody)
            ?? throw new AnthropicChatRequestException("Request body must be a JSON object.");

        JsonNode? messages = root["messages"];
        if (messages is null)
        {
            throw new AnthropicChatRequestException("messages: Field required");
        }

        if (messages is not JsonArray)
        {
            throw new AnthropicChatRequestException("messages: Input should be a valid list");
        }

        int estimate = AnthropicChatTokenEstimator.RequestEstimate(root, options);
        return AnthropicChatJson.CompactObject(new JsonObject { ["input_tokens"] = estimate });
    }

    private static AnthropicChatReply FailureReply(AnthropicChatFailure failure, string? retryAfter, string? retryAfterMs) =>
        new(failure.StatusCode, Envelope(failure.Type, failure.Message), retryAfter, retryAfterMs);

    private static string Envelope(string type, string message) =>
        AnthropicChatJson.CompactObject(new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = type, ["message"] = message }
        });

    private static bool IsNonEmptyError(JsonNode? node) =>
        node switch
        {
            JsonObject obj => obj.Count > 0,
            JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String =>
                !string.IsNullOrEmpty(value.GetValue<string>()),
            _ => false
        };

    private static string ErrorType(string body)
    {
        JsonObject? root = AnthropicChatJson.TryParseObject(body);
        return root?["error"] is JsonObject error
            ? AnthropicChatJson.String(error["type"]) ?? "api_error"
            : "api_error";
    }

    private static string ErrorMessage(string body)
    {
        JsonObject? root = AnthropicChatJson.TryParseObject(body);
        return root?["error"] is JsonObject error
            ? AnthropicChatJson.String(error["message"]) ?? "Upstream reported an error"
            : "Upstream reported an error";
    }
}
