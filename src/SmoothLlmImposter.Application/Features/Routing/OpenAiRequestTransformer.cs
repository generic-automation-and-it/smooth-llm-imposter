using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// OpenAI-dialect transform: rewrites <c>model</c> and, when caching is enabled, sets
/// <c>prompt_cache_key</c> to the original (inbound) model so requests for the same imposter model
/// share an upstream cache bucket. OpenAI caches automatically, so no content restructuring is needed.
/// </summary>
internal sealed class OpenAiRequestTransformer : IRequestTransformer
{
    public ApiDialect Dialect => ApiDialect.OpenAi;

    public string Transform(string requestBody, RouteDecision decision, string inboundModel)
    {
        JsonObject root = ParseObject(requestBody);

        root["model"] = decision.TargetModel;

        if (decision.CachingEnabled)
        {
            root["prompt_cache_key"] = inboundModel;
        }

        return root.ToJsonString();
    }

    private static JsonObject ParseObject(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject
                ?? throw new RoutingException("Request body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new RoutingException($"Request body is not valid JSON: {ex.Message}");
        }
    }
}
