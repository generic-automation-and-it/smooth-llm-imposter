using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>Builds dialect-shaped error envelopes (OpenAI vs Anthropic) for failures the Host returns.</summary>
public interface IErrorResponseFactory
{
    /// <returns>A JSON error body matching the dialect's native error shape.</returns>
    string Create(ApiDialect dialect, string message, string type);

    /// <summary>
    /// Builds the terminal SSE frame for a stream that failed after its headers were already on the wire, when a
    /// JSON error body is no longer writable.
    /// </summary>
    /// <returns>A complete SSE frame, terminating blank line included.</returns>
    string CreateStreamErrorFrame(StreamErrorFraming framing, string message, string type);
}
