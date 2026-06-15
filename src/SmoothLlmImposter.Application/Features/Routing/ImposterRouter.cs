using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

internal sealed class ImposterRouter : IImposterRouter
{
    private readonly IRouteResolver _resolver;
    private readonly IReadOnlyDictionary<ApiDialect, IRequestTransformer> _transformers;
    private readonly ILogger<ImposterRouter> _logger;

    public ImposterRouter(
        IRouteResolver resolver,
        IEnumerable<IRequestTransformer> transformers,
        ILogger<ImposterRouter> logger)
    {
        _resolver = resolver;
        _transformers = transformers.ToDictionary(t => t.Dialect);
        _logger = logger;
    }

    public RoutePlan Plan(ApiDialect dialect, string requestBody)
    {
        string model = ExtractModel(requestBody);
        RouteDecision decision = _resolver.Resolve(dialect, model);

        if (!_transformers.TryGetValue(dialect, out IRequestTransformer? transformer))
        {
            throw new RoutingException($"No request transformer registered for dialect '{dialect}'.", statusCode: 500);
        }

        string transformedBody = transformer.Transform(requestBody, decision, model);

        _logger.LogInformation(
            "Routed {Dialect} model '{InboundModel}' -> provider '{Provider}' as '{TargetModel}' (imposter={IsImposter}, caching={Caching})",
            dialect,
            model,
            decision.Provider.Name,
            decision.TargetModel,
            decision.IsImposter,
            decision.CachingEnabled);

        return new RoutePlan(decision, model, transformedBody);
    }

    private static string ExtractModel(string requestBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(requestBody);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RoutingException("Request body must be a JSON object.");
            }

            if (!document.RootElement.TryGetProperty("model", out JsonElement modelElement) ||
                modelElement.ValueKind != JsonValueKind.String)
            {
                throw new RoutingException("Request is missing a string 'model' property.");
            }

            return modelElement.GetString()!;
        }
        catch (JsonException ex)
        {
            throw new RoutingException($"Request body is not valid JSON: {ex.Message}");
        }
    }
}
