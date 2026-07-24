using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Anthropic-dialect transform: rewrites <c>model</c> and, when caching is enabled, injects ephemeral
/// <c>cache_control</c> breakpoints the upstream would not add itself — on the <c>system</c> prompt and
/// on the last content block of the last message (the two most effective, stable breakpoints).
/// Mirrors the proxy's prompt-cache injection (LADR-06).
/// </summary>
internal sealed class AnthropicRequestTransformer : IRequestTransformer
{
    public ApiDialect Dialect => ApiDialect.Anthropic;

    public string Transform(
        string requestBody,
        RouteDecision decision,
        string inboundModel,
        SessionIdentity sessionIdentity)
    {
        // Anthropic body injection is out of scope for HLD 009 (header-only stamping lives in the
        // forwarder). sessionIdentity is accepted for interface uniformity; it is body-only unused
        // here — the resolved identity is still propagated via RoutePlan.SessionIdentity to the
        // forwarder for the header write.
        _ = sessionIdentity;

        JsonObject root = JsonNodeMaterializer.ParseObject(requestBody);

        root["model"] = decision.TargetModel;

        if (decision.CachingEnabled)
        {
            InjectSystemCacheControl(root);
            InjectLastMessageCacheControl(root);
        }

        return root.ToJsonString();
    }

    private static void InjectSystemCacheControl(JsonObject root)
    {
        if (!root.TryGetPropertyValue("system", out JsonNode? system) || system is null)
        {
            return;
        }

        // "system": "text"  ->  array of one text block carrying the cache breakpoint.
        if (system is JsonValue value && value.TryGetValue(out string? text))
        {
            root["system"] = new JsonArray(TextBlockWithCacheControl(text));
            return;
        }

        if (system is JsonArray array)
        {
            MarkLastObject(array);
        }
    }

    private static void InjectLastMessageCacheControl(JsonObject root)
    {
        if (root["messages"] is not JsonArray { Count: > 0 } messages)
        {
            return;
        }

        if (messages[^1] is not JsonObject lastMessage)
        {
            return;
        }

        JsonNode? content = lastMessage["content"];

        if (content is JsonValue value && value.TryGetValue(out string? text))
        {
            lastMessage["content"] = new JsonArray(TextBlockWithCacheControl(text));
            return;
        }

        if (content is JsonArray array)
        {
            MarkLastObject(array);
        }
    }

    private static void MarkLastObject(JsonArray array)
    {
        for (int i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is JsonObject block)
            {
                block["cache_control"] = EphemeralCacheControl();
                return;
            }
        }
    }

    private static JsonObject TextBlockWithCacheControl(string? text) => new()
    {
        ["type"] = "text",
        ["text"] = text ?? string.Empty,
        ["cache_control"] = EphemeralCacheControl()
    };

    private static JsonObject EphemeralCacheControl() => new() { ["type"] = "ephemeral" };
}
