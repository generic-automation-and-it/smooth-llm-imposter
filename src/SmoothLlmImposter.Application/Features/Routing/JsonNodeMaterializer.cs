using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing;

internal static class JsonNodeMaterializer
{
    public static JsonObject ParseObject(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? MaterializeObject(document.RootElement)
                : throw new RoutingException("Request body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new RoutingException($"Request body is not valid JSON: {ex.Message}");
        }
    }

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
            JsonValueKind.Number => MaterializeNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };

    private static JsonNode MaterializeNumber(JsonElement element)
    {
        if (element.TryGetInt64(out long signed))
        {
            return JsonValue.Create(signed)!;
        }

        if (element.TryGetUInt64(out ulong unsigned))
        {
            return JsonValue.Create(unsigned)!;
        }

        if (element.TryGetDecimal(out decimal exact))
        {
            return JsonValue.Create(exact)!;
        }

        return JsonValue.Create(element.GetDouble())!;
    }
}
