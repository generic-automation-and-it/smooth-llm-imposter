namespace SmoothLlmImposter.Domain.Routing;

/// <summary>OpenAI-dialect upstream API surface a provider supports.</summary>
public enum OpenAiUpstreamApi
{
    Responses = 0,
    ChatCompletions = 1
}

public static class OpenAiUpstreamApiParser
{
    public static bool TryParse(string? value, out OpenAiUpstreamApi api)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            api = OpenAiUpstreamApi.Responses;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "responses":
                api = OpenAiUpstreamApi.Responses;
                return true;
            case "chat":
            case "chat_completions":
            case "chat-completions":
                api = OpenAiUpstreamApi.ChatCompletions;
                return true;
            default:
                api = OpenAiUpstreamApi.Responses;
                return false;
        }
    }

    public static OpenAiUpstreamApi Parse(string? value) =>
        TryParse(value, out OpenAiUpstreamApi api)
            ? api
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown OpenAI upstream API.");
}
