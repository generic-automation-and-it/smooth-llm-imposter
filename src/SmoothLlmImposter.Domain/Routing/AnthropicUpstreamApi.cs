namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// Anthropic-dialect upstream API surface a provider supports. <see cref="Messages"/> is the native
/// Anthropic Messages API and the default; <see cref="ChatCompletions"/> opts a matched imposter route
/// into the Messages-to-Chat bridge (HLD 016).
/// </summary>
public enum AnthropicUpstreamApi
{
    Messages = 0,
    ChatCompletions = 1
}

public static class AnthropicUpstreamApiParser
{
    public static bool TryParse(string? value, out AnthropicUpstreamApi api)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            api = AnthropicUpstreamApi.Messages;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "messages":
                api = AnthropicUpstreamApi.Messages;
                return true;
            case "chat":
            case "chat_completions":
            case "chat-completions":
                api = AnthropicUpstreamApi.ChatCompletions;
                return true;
            default:
                api = AnthropicUpstreamApi.Messages;
                return false;
        }
    }

    public static AnthropicUpstreamApi Parse(string? value) =>
        TryParse(value, out AnthropicUpstreamApi api)
            ? api
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Anthropic upstream API.");
}
