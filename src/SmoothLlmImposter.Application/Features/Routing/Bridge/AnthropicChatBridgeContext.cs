using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Request-derived context handed from the request mapper to the reply mappers (HLD 016 spec 01 section 14,
/// D17). Nothing is persisted between requests.
/// </summary>
internal sealed record AnthropicChatBridgeContext(
    string InboundModel,
    bool Stream,
    AnthropicChatToolIdentity ToolIdentity,
    int RequestTokenEstimate,
    AnthropicChatBridgeOptions Options);

/// <summary>The Chat body plus the context the reply side needs.</summary>
internal sealed record AnthropicChatRequestResult(string ChatBody, AnthropicChatBridgeContext Context);

/// <summary>
/// A request that the bridge rejects before it reaches the upstream. The Host turns it into HTTP 400 with
/// the Messages error envelope (spec 01 section 3).
/// </summary>
public sealed class AnthropicChatRequestException(string message) : Exception(message);

/// <summary>A bridge-detected failure that must reach the client in the Messages error contract.</summary>
internal sealed class AnthropicChatFailureException(AnthropicChatFailure failure) : Exception(failure.Message)
{
    public AnthropicChatFailure Failure { get; } = failure;
}

/// <summary>
/// A 2xx Chat reply whose selected choice carries an in-band `error` and no usable content (spec 02 section
/// 2 step 7 / section 11.5). The bridge maps it with the configured-secret redaction it owns.
/// </summary>
internal sealed class AnthropicChatChoiceErrorException(JsonObject choice) : Exception("Upstream returned an in-band error.")
{
    public JsonObject Choice { get; } = choice;
}
