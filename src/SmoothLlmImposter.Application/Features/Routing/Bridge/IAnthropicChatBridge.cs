using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>The mapped request plus the opaque request-derived context the reply side needs.</summary>
public sealed class AnthropicChatCall
{
    internal AnthropicChatCall(AnthropicChatRequestResult result)
    {
        ChatBody = result.ChatBody;
        Stream = result.Context.Stream;
        Context = result.Context;
    }

    /// <summary>The Chat Completions body to forward upstream.</summary>
    public string ChatBody { get; }

    /// <summary>Whether the client asked for a stream.</summary>
    public bool Stream { get; }

    internal AnthropicChatBridgeContext Context { get; }
}

/// <summary>A Messages-shaped reply or failure ready for the Host to write.</summary>
public sealed record AnthropicChatReply(
    int StatusCode,
    string Body,
    string? RetryAfter = null,
    string? RetryAfterMs = null);

/// <summary>
/// The Application-owned Messages-to-Chat bridge (HLD 016). The Host plans the route, calls this to map
/// the request, forwards once, and calls it again to map the reply. The bridge never forwards or retries.
/// </summary>
public interface IAnthropicChatBridge
{
    /// <summary>Maps an inbound Messages body to a Chat Completions body; throws <see cref="AnthropicChatRequestException"/> on validation failure.</summary>
    AnthropicChatCall MapRequest(string messagesBody, string inboundModel, AnthropicChatBridgeOptions options);

    /// <summary>Maps a non-streaming upstream reply (success or failure) to a Messages reply.</summary>
    AnthropicChatReply MapResponse(
        int upstreamStatus,
        string body,
        string? retryAfter,
        string? retryAfterMs,
        AnthropicChatCall call,
        string? configuredSecret);

    /// <summary>Translates a Chat SSE line stream to Messages SSE frames.</summary>
    IAsyncEnumerable<string> TranslateStreamAsync(
        IAsyncEnumerable<string> upstreamLines,
        AnthropicChatCall call,
        CancellationToken cancellationToken);

    /// <summary>Replays a complete mapped reply as a Messages event sequence (2xx non-SSE to a streaming request).</summary>
    IEnumerable<string> ReplayAsStream(AnthropicChatReply reply, AnthropicChatCall call);

    /// <summary>Answers <c>POST /v1/messages/count_tokens</c> locally.</summary>
    string CountTokens(string messagesBody, AnthropicChatBridgeOptions options);
}
