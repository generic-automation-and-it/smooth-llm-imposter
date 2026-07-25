using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Default <see cref="IWhoMessageResponder"/>. Reads the raw inbound body, locates the last
/// <c>role:"user"</c> message, and matches its text content (string or concatenated text parts)
/// against the exact trigger <c>who?</c> (trimmed, case-sensitive). On match, builds a
/// dialect-shaped chat envelope whose content text names the inbound model, the resolved
/// upstream target (or <c>passthrough</c>), and the auth scheme resolved by
/// <see cref="ImposterRouter.DescribeAuth"/>.
/// </summary>
/// <remarks>
/// <para>
/// Trigger semantics (HLD 010, LADR-02):
/// <list type="bullet">
///   <item><description>stream: true → no match (LADR-05 — no SSE synthesis).</description></item>
///   <item><description>no <c>messages</c> array → no match.</description></item>
///   <item><description>no <c>role:"user"</c> message → no match.</description></item>
///   <item><description>last user message has non-text content (image, tool_use, etc.) → no match.</description></item>
///   <item><description>last user text, trimmed, must equal <c>who?</c> ordinally.</description></item>
/// </list>
/// </para>
/// <para>
/// NFR-03: the reply exposes only inbound-model, resolved target, and auth-scheme name.
/// No secret, credential, base URL, or provider registry key ever reaches the output; the
/// auth token comes from <see cref="ImposterRouter.DescribeAuth"/>, which returns only the
/// scheme name (<c>Bearer</c> / <c>ApiKey</c> / <c>none</c> / <c>caller-passthrough</c>).
/// </para>
/// </remarks>
internal sealed class WhoMessageResponder : IWhoMessageResponder
{
    internal const string Trigger = "who?";
    private const string PassthroughLabel = "passthrough";

    public bool TryBuildResponse(ApiDialect dialect, string requestBody, RoutePlan plan, out string? responseJson)
    {
        responseJson = null;

        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestBody);
        }
        catch (JsonException)
        {
            // Router has already validated upstream of this call; defensive return keeps the responder
            // a pure predicate. Callers of the public method do not expect a throw on malformed input.
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Streaming requests pass through (LADR-05). The forwarder keys off the body's `stream`
            // field, so the probe must too — an Accept header alone would disagree with the forward path.
            if (root.TryGetProperty("stream", out JsonElement streamElement) &&
                streamElement.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            if (!root.TryGetProperty("messages", out JsonElement messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            string? lastUserText = ExtractLastUserText(messages);
            if (lastUserText is null || !string.Equals(lastUserText.Trim(), Trigger, StringComparison.Ordinal))
            {
                return false;
            }
        }

        string contentText = BuildContentText(plan, ImposterRouter.DescribeAuth(plan.Decision, dialect, plan.CredentialOverride));
        string model = string.IsNullOrEmpty(plan.InboundModel) ? PassthroughLabel : plan.InboundModel;

        responseJson = dialect == ApiDialect.OpenAi
            ? BuildOpenAiEnvelope(model, contentText)
            : BuildAnthropicEnvelope(model, contentText);

        return true;
    }

    /// <summary>
    /// Walks the messages array once and returns the text content of the LAST message whose
    /// <c>role</c> is <c>user</c>, or <c>null</c> when no user message exists, the last user
    /// message carries non-text content, or its <c>content</c> field is missing/non-string/non-array.
    /// </summary>
    private static string? ExtractLastUserText(JsonElement messages)
    {
        string? lastUserText = null;

        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!message.TryGetProperty("role", out JsonElement roleElement) ||
                roleElement.ValueKind != JsonValueKind.String ||
                !string.Equals(roleElement.GetString(), "user", StringComparison.Ordinal))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out JsonElement content))
            {
                // A user message with no content cannot carry the trigger; treat as absent.
                lastUserText = null;
                continue;
            }

            lastUserText = ReadTextContent(content);
        }

        return lastUserText;
    }

    /// <summary>
    /// Returns the plain-text value of a message <c>content</c> field when it is (a) a string or
    /// (b) an array of text-only parts, otherwise <c>null</c>. Any non-text part in the array
    /// (image, tool_use, tool_result, …) short-circuits to <c>null</c> so the trigger cannot fire
    /// on multimodal input. A text part missing its <c>text</c> field is skipped (permissive) to
    /// remain forward-compatible with future OpenAI/Anthropic content shapes; an array of only
    /// such parts returns the empty string rather than <c>null</c>.
    /// </summary>
    private static string? ReadTextContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!part.TryGetProperty("type", out JsonElement typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeElement.GetString(), "text", StringComparison.Ordinal))
                {
                    return null;
                }

                if (part.TryGetProperty("text", out JsonElement textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textElement.GetString());
                }
            }

            return builder.ToString();
        }

        return null;
    }

    private static string BuildContentText(RoutePlan plan, string auth)
    {
        if (plan.Decision.IsImposter)
        {
            return $"Imposter: {plan.InboundModel} → {plan.Decision.TargetModel} (auth: {auth})";
        }

        // Passthrough: the upstream target IS the inbound model (no rewrite); surface the inbound
        // name once and the auth scheme. Empty inbound model means the request had no `model` field,
        // which cannot happen here — PlanAsync requires it — but guard anyway with the passthrough label.
        string inbound = string.IsNullOrEmpty(plan.InboundModel) ? PassthroughLabel : plan.InboundModel;
        return $"Passthrough: {inbound} (auth: {auth})";
    }

    // Synthetic ids use a `who-` prefix so transcripts and logs can grep for probe replies
    // without colliding with real upstream ids. Created is wall-clock because (unlike the
    // /v1/models catalogue) probe replies are one-shot debugging affordances, not byte-identical
    // discovery responses; NFR-01 applies to the NON-match path only.
    private static string BuildOpenAiEnvelope(string model, string contentText)
    {
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var envelope = new JsonObject
        {
            ["id"] = $"chatcmpl-who-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = contentText,
                    },
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0,
            },
        };

        return envelope.ToJsonString();
    }

    private static string BuildAnthropicEnvelope(string model, string contentText)
    {
        // `created_at` is intentionally omitted (LADR-03 is silent on it; Anthropic SDK accepts
        // messages without it; the sibling AnthropicModelCatalogResponder sets a fixed epoch
        // because /v1/models discovery must be byte-identical across calls — the who-probe does
        // not have that constraint).
        var envelope = new JsonObject
        {
            ["id"] = $"msg_who_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = contentText,
                },
            },
            ["model"] = model,
            ["stop_reason"] = "end_turn",
            // `stop_sequence` is omitted (convention for end_turn with no configured sequence; matches
            // the Anthropic SDK's wire pattern — a present null is accepted but not emitted).
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 0,
                ["output_tokens"] = 0,
            },
        };

        return envelope.ToJsonString();
    }
}
