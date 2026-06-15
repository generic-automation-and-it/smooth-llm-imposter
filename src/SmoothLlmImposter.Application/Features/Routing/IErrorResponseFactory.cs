using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>Builds dialect-shaped error envelopes (OpenAI vs Anthropic) for failures the Host returns.</summary>
public interface IErrorResponseFactory
{
    /// <returns>A JSON error body matching the dialect's native error shape.</returns>
    string Create(ApiDialect dialect, string message, string type);
}
