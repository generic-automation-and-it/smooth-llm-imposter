namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Translates Chat Completions responses back into Responses wire shape for the scoped
/// OpenAI <c>/responses</c> to Chat Completions downgrade path.
/// </summary>
public interface IChatToResponsesTransformer
{
    IAsyncEnumerable<string> TransformStreamingAsync(
        IAsyncEnumerable<string> upstreamLines,
        CancellationToken cancellationToken);

    string TransformNonStreaming(string chatCompletionJson);
}
