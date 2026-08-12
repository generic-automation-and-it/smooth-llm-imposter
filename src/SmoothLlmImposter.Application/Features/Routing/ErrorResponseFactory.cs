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

    public string CreateStreamErrorFrame(StreamErrorFraming framing, string message, string type) => framing switch
    {
        // Anthropic names every stream event, so the terminal error reuses the dialect's JSON envelope verbatim.
        StreamErrorFraming.Anthropic => Frame("error", Create(ApiDialect.Anthropic, message, type)),

        // Responses events carry the discriminator inside `data` rather than nesting under `error`.
        StreamErrorFraming.OpenAiResponses => Frame("error", new JsonObject
        {
            ["type"] = "error",
            ["code"] = type,
            ["message"] = message,
            ["param"] = null
        }.ToJsonString()),

        // Chat Completions streams are unnamed data frames; an `event:` line here would be ignored at best.
        _ => $"data: {Create(ApiDialect.OpenAi, message, type)}\n\n"
    };

    private static string Frame(string eventName, string dataJson) => $"event: {eventName}\ndata: {dataJson}\n\n";
}
