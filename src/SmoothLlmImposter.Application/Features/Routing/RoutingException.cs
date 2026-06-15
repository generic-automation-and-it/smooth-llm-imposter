namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Thrown when an inbound request cannot be routed — e.g. it carries no <c>model</c>, or no mapping
/// matched and the dialect has no default provider. Surfaced to the client as a dialect-shaped error.
/// </summary>
public sealed class RoutingException(string message, int statusCode = 400) : Exception(message)
{
    /// <summary>HTTP status to return to the caller (400 for bad request, 404 for unroutable model).</summary>
    public int StatusCode { get; } = statusCode;
}
