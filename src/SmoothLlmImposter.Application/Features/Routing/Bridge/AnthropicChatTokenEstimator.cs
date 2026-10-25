using System.Text;
using System.Text.Json.Nodes;

using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// The single token estimator shared by <c>count_tokens</c>, the request-derived estimate, and the
/// missing-usage fallback (HLD 016 spec 04 section 1.4). Pure, deterministic, integer-only.
/// </summary>
internal static class AnthropicChatTokenEstimator
{
    private const int UnitsPerToken = 4;

    /// <summary>Estimates tokens for a string; <c>EST("") = 0</c> (spec 04 section 1.4.1).</summary>
    public static int EstimateText(string text) =>
        (int)((Units(text) + UnitsPerToken - 1) / UnitsPerToken);

    /// <summary>
    /// The request estimate: 12 base units plus system, messages and tools (spec 04 section 1.4.2).
    /// </summary>
    public static int RequestEstimate(JsonObject body, AnthropicChatBridgeOptions options) =>
        (RequestUnits(body, options) + UnitsPerToken - 1) / UnitsPerToken;

    /// <summary>The raw request unit count, before rounding (spec 04 section 1.4.4 additivity).</summary>
    internal static int RequestUnits(JsonObject body, AnthropicChatBridgeOptions options)
    {
        int units = 12;

        units += SystemUnits(body["system"]);

        if (body["messages"] is JsonArray messages)
        {
            foreach (JsonNode? messageNode in messages)
            {
                if (messageNode is not JsonObject message)
                {
                    continue;
                }

                units += 16;
                units += MessageContentUnits(message["content"], options);
            }
        }

        if (body["tools"] is JsonArray tools)
        {
            foreach (JsonNode? toolNode in tools)
            {
                if (toolNode is JsonObject tool)
                {
                    units += ToolUnits(tool);
                }
            }
        }

        return units;
    }

    private static int SystemUnits(JsonNode? system)
    {
        if (AnthropicChatJson.String(system) is { } text)
        {
            return Units(text);
        }

        if (system is not JsonArray blocks)
        {
            return 0;
        }

        var pieces = new List<string>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i == 0 && AnthropicChatJson.IsAttributionBlock(blocks[i]))
            {
                continue;
            }

            if (blocks[i] is JsonObject block &&
                string.Equals(AnthropicChatJson.String(block["type"]), "text", StringComparison.Ordinal) &&
                AnthropicChatJson.String(block["text"]) is { } blockText)
            {
                pieces.Add(blockText);
            }
        }

        return Units(string.Join('\n', pieces));
    }

    private static int MessageContentUnits(JsonNode? content, AnthropicChatBridgeOptions options)
    {
        if (AnthropicChatJson.String(content) is { } text)
        {
            return Units(text);
        }

        if (content is not JsonArray blocks)
        {
            return 0;
        }

        int units = 0;
        foreach (JsonNode? node in blocks)
        {
            if (node is JsonObject block)
            {
                units += BlockUnits(block, options);
            }
        }

        return units;
    }

    private static int BlockUnits(JsonObject block, AnthropicChatBridgeOptions options)
    {
        string type = AnthropicChatJson.BlockType(block);

        switch (type)
        {
            case "text":
                return AnthropicChatJson.String(block["text"]) is { } text ? Units(text) : 0;

            case "image":
                return 6000;

            case "tool_use":
            {
                int nameUnits = Units(AnthropicChatJson.String(block["name"]) ?? string.Empty);
                JsonNode input = block["input"] ?? new JsonObject();
                return 24 + nameUnits + Units(AnthropicChatJson.Compact(input));
            }

            case "tool_result":
                return 24 + ToolResultContentUnits(block["content"], options);

            case "thinking":
            case "redacted_thinking":
                return 0;

            case "document":
                return options.DocumentMode == AnthropicChatDocumentMode.Image && IsImageDocument(block)
                    ? 6000
                    : Units(AnthropicChatJson.Placeholder("document"));

            default:
                return Units(AnthropicChatJson.Placeholder(type));
        }
    }

    private static int ToolResultContentUnits(JsonNode? content, AnthropicChatBridgeOptions options)
    {
        if (AnthropicChatJson.String(content) is { } text)
        {
            return Units(text);
        }

        if (content is not JsonArray blocks)
        {
            return 0;
        }

        var textPieces = new List<string>();
        int units = 0;

        foreach (JsonNode? node in blocks)
        {
            if (node is not JsonObject block)
            {
                continue;
            }

            string type = AnthropicChatJson.BlockType(block);
            switch (type)
            {
                case "text":
                    if (AnthropicChatJson.String(block["text"]) is { } blockText)
                    {
                        textPieces.Add(blockText);
                    }
                    break;

                case "image":
                    units += 6000;
                    break;

                case "tool_reference":
                    if (AnthropicChatJson.String(block["tool_name"]) is not null)
                    {
                        units += Units(AnthropicChatJson.Compact(block));
                    }
                    break;

                default:
                    units += Units(AnthropicChatJson.Placeholder(type));
                    break;
            }
        }

        return Units(string.Join('\n', textPieces)) + units;
    }

    private static int ToolUnits(JsonObject tool)
    {
        string? type = AnthropicChatJson.String(tool["type"]);
        if (type is not null && !string.Equals(type, "custom", StringComparison.Ordinal))
        {
            return 0;
        }

        int units = 48;
        units += Units(AnthropicChatJson.String(tool["name"]) ?? string.Empty);
        units += Units(AnthropicChatJson.String(tool["description"]) ?? string.Empty);
        units += Units(AnthropicChatJson.Compact(tool["input_schema"] ?? DefaultSchema()));
        return units;
    }

    private static JsonObject DefaultSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject()
    };

    /// <summary>True when a document block can be sent as an image part under <c>documentMode: image</c>.</summary>
    public static bool IsImageDocument(JsonObject block)
    {
        if (block["source"] is not JsonObject source)
        {
            return false;
        }

        string? sourceType = AnthropicChatJson.String(source["type"]);
        if (string.Equals(sourceType, "base64", StringComparison.Ordinal))
        {
            string? mediaType = AnthropicChatJson.String(source["media_type"])?.ToLowerInvariant();
            return mediaType is not null &&
                (mediaType == "application/pdf" || mediaType.StartsWith("image/", StringComparison.Ordinal));
        }

        if (string.Equals(sourceType, "url", StringComparison.Ordinal))
        {
            return !string.IsNullOrEmpty(AnthropicChatJson.String(source["url"]));
        }

        return false;
    }

    /// <summary>The output estimate over the concatenated emitted pieces (spec 04 section 1.4.3).</summary>
    public static int OutputEstimate(string concatenated) => EstimateText(concatenated);

    /// <summary>The unit weight of one Unicode scalar (spec 04 section 1.4.1).</summary>
    internal static int ScalarWeight(int scalar)
    {
        if (IsAsciiLetterOrDigitOrSpaceOrControl(scalar))
        {
            return 1;
        }

        if (scalar <= 0x7F)
        {
            return 2;
        }

        if (scalar is >= 0x80 and <= 0x7FF)
        {
            return 2;
        }

        if (scalar is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF
            or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF
            or >= 0x20000 and <= 0x2FFFF)
        {
            return 6;
        }

        if (scalar is >= 0x10000 and <= 0x1FFFF)
        {
            return 8;
        }

        return 4;
    }

    private static bool IsAsciiLetterOrDigitOrSpaceOrControl(int scalar) =>
        scalar is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or ' '
            or '\t'
            or '\n'
            or '\r';

    /// <summary>The raw unit count of a string; the stream translator accumulates units without retaining text.</summary>
    internal static int UnitsOf(string text) => Units(text);

    private static int Units(string text)
    {
        int units = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            units += ScalarWeight(rune.Value);
        }

        return units;
    }
}
