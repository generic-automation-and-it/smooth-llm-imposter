using System.Text;
using System.Text.Json;
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

    public static string ReplaceTopLevel(string body, JsonObject root, params string[] names)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(body);
        var reader = new Utf8JsonReader(utf8);
        reader.Read();
        var properties = new List<string>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            int start = (int)reader.TokenStartIndex;
            string name = reader.GetString()!;
            reader.Read();
            reader.Skip();
            if (!names.Contains(name))
            {
                properties.Add(Encoding.UTF8.GetString(utf8, start, (int)reader.BytesConsumed - start));
            }
            else if (root[name] is { } replacement)
            {
                properties.Add($"{JsonValue.Create(name).ToJsonString()}:{replacement.ToJsonString()}");
            }
        }

        return $"{{{string.Join(",", properties)}}}";
    }
}
