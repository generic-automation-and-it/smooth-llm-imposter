namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Wire shape of a terminal SSE error frame. The dialect alone is not enough: the OpenAI dialect streams two
/// incompatible event shapes — Chat Completions emits unnamed <c>data:</c> frames, while Responses emits named
/// <c>event:</c> frames — and a client parses only the one its endpoint speaks.
/// </summary>
public enum StreamErrorFraming
{
    /// <summary>Unnamed <c>data:</c> frame carrying the OpenAI error envelope (Chat Completions streams).</summary>
    OpenAiChat,

    /// <summary>Named <c>event: error</c> frame carrying a Responses-shaped error payload.</summary>
    OpenAiResponses,

    /// <summary>Named <c>event: error</c> frame carrying the Anthropic error envelope.</summary>
    Anthropic
}
