using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Shapes errors into the caller's native dialect so SDKs parse them as real upstream errors.
/// OpenAI: <c>{ "error": { "message", "type" } }</c>. Anthropic: <c>{ "type": "error", "error": { "type", "message" } }</c>.
/// </summary>
internal sealed class ErrorResponseFactory : IErrorResponseFactory
{
    public string Create(ApiDialect dialect, string message, string type)
    {
        JsonObject envelope = dialect switch
        {
            ApiDialect.Anthropic => new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject
                {
                    ["type"] = type,
                    ["message"] = message
                }
            },
            _ => new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = message,
                    ["type"] = type
                }
            }
        };

        return envelope.ToJsonString();
    }
}
