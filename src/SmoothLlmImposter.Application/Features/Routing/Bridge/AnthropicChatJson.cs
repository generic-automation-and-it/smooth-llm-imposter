using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Strict JSON parsing and compact writing for the Messages-to-Chat bridge (HLD 016 spec 01 section 16,
/// spec 02 section 14). Numbers keep their textual form: parsing materialises from <see cref="JsonElement"/>
/// raw text rather than round-tripping through <c>double</c> or <c>decimal</c>.
/// </summary>
internal static class AnthropicChatJson
{
    internal const int MaxDepth = 64;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxDepth
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>Parses a JSON text; returns null when it is not a JSON object (spec 01 R01 / spec 02 section 2).</summary>
    public static JsonObject? TryParseObject(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? MaterializeObject(document.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Compact(JsonNode? node) => node is null ? "null" : node.ToJsonString(CompactOptions);

    public static string CompactObject(JsonObject node) => node.ToJsonString(CompactOptions);

    private static JsonObject MaterializeObject(JsonElement element)
    {
        var obj = new JsonObject();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            obj[property.Name] = Materialize(property.Value);
        }

        return obj;
    }

    private static JsonArray MaterializeArray(JsonElement element)
    {
        var array = new JsonArray();
        foreach (JsonElement item in element.EnumerateArray())
        {
            array.Add(Materialize(item));
        }

        return array;
    }

    private static JsonNode? Materialize(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => MaterializeObject(element),
            JsonValueKind.Array => MaterializeArray(element),
            JsonValueKind.String => JsonValue.Create(element.GetString()),
            // JsonNode.Parse of the raw token keeps 1.0, 1e2 and integers beyond 2^53 byte-identical.
            JsonValueKind.Number => JsonNode.Parse(element.GetRawText(), documentOptions: DocumentOptions),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            _ => null
        };

    /// <summary>Returns the value only when it is exactly a JSON string; otherwise null.</summary>
    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    /// <summary>Returns the value only when it is exactly a JSON number; otherwise null.</summary>
    public static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
        value.TryGetValue(out double number) && double.IsFinite(number)
            ? number
            : null;

    /// <summary>Returns the value only when it is exactly a JSON boolean; otherwise null.</summary>
    public static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : null;

    /// <summary>
    /// True when the value is a JSON number with a zero fractional part and at least <paramref name="minimum"/>.
    /// </summary>
    public static bool TryGetInteger(JsonNode? node, int minimum, out int result)
    {
        result = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        // Decimal keeps the textual value (1.0 vs 1) distinguishable and avoids double rounding.
        if (!decimal.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out decimal parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed < minimum ||
            parsed > int.MaxValue)
        {
            return false;
        }

        result = (int)parsed;
        return true;
    }

    /// <summary>Returns true when the text is empty or consists only of Unicode white-space characters.</summary>
    public static bool IsWhiteSpaceOnly(string text) => text.AsSpan().Trim().IsEmpty;

    /// <summary>The client's per-request attribution marker (spec 01 D18 / spec 04 section 3.1).</summary>
    public static bool IsAttributionBlock(JsonNode? block) =>
        block is JsonObject obj &&
        string.Equals(String(obj["type"]), "text", StringComparison.Ordinal) &&
        String(obj["text"]) is { } text &&
        text.StartsWith("x-anthropic-billing-header:", StringComparison.OrdinalIgnoreCase);

    public static string Placeholder(string type) => $"[omitted: unsupported content block '{type}']";

    public static string BlockType(JsonObject block) => String(block["type"]) ?? "unknown";
}
