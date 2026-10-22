using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal enum DecisionBodyShape { Unknown, Chat, Responses, Anthropic }

internal static class DecisionJson
{
    public static DecisionBodyShape ShapeFor(string path) => path switch
    {
        _ when path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) => DecisionBodyShape.Chat,
        _ when path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) => DecisionBodyShape.Responses,
        _ when path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) => DecisionBodyShape.Anthropic,
        _ => DecisionBodyShape.Unknown
    };

    public static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;

    public static JsonObject[] Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>().ToArray() : [];
}
