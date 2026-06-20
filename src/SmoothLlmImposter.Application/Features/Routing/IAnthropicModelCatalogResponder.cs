namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Builds the <c>GET /anthropic/v1/models</c> discovery response locally from the route catalogue —
/// the distinct union of every <c>to</c> target across Anthropic-dialect provider mappings, shaped as
/// a valid Anthropic List Models body. The router answers this path itself instead of forwarding it
/// upstream (HLD 005, Anthropic scope). Returns the serialized JSON string (string-out, no
/// <c>HttpContext</c>, no upstream call, no DB read — recognition lives in the Host).
/// </summary>
public interface IAnthropicModelCatalogResponder
{
    /// <summary>Serializes the Anthropic List Models response for the configured Anthropic catalogue.</summary>
    string BuildModelsResponse();
}
