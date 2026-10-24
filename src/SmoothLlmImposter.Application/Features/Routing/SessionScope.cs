using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing;

internal static class SessionScope
{
    private static readonly string[] CallerHeaderNames =
        ["authorization", "x-api-key", "chatgpt-account-id", "openai-organization", "openai-project"];

    public static string Key(string kind, string model, string identity, CallerHeaders headers)
    {
        var scope = new JsonArray(kind, model, identity);
        foreach (string header in CallerHeaderNames)
        {
            var values = new JsonArray();
            foreach (string value in headers.Get(header) ?? [])
            {
                values.Add((JsonNode)JsonValue.Create(value));
            }
            scope.Add((JsonNode)values);
        }
        return Hash(scope.ToJsonString());
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
