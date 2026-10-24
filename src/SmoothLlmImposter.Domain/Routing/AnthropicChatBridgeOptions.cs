namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// The per-provider tuning surface consumed by the Messages-to-Chat bridge (HLD 016 spec 01 section 2).
/// An immutable snapshot carried by <see cref="ProviderRoute"/>; every option is inert unless the provider
/// opts in with <see cref="AnthropicUpstreamApi.ChatCompletions"/>.
/// </summary>
public sealed record AnthropicChatBridgeOptions(
    AnthropicUpstreamApi UpstreamApi = AnthropicUpstreamApi.Messages,
    AnthropicChatCompatProfile CompatProfile = AnthropicChatCompatProfile.None,
    bool SendReasoningEffort = false,
    AnthropicChatUserIdMode UserIdMode = AnthropicChatUserIdMode.Off,
    AnthropicChatMaxTokensField MaxTokensField = AnthropicChatMaxTokensField.MaxTokens,
    int? MaxOutputTokens = null,
    bool ForwardCacheControl = false,
    bool SendResponseFormat = false,
    AnthropicChatDocumentMode DocumentMode = AnthropicChatDocumentMode.Placeholder,
    bool ThinkMarkupExtraction = false,
    AnthropicChatMidturnSystem MidturnSystem = AnthropicChatMidturnSystem.System)
{
    public bool IsBridged => UpstreamApi == AnthropicUpstreamApi.ChatCompletions;
}

/// <summary>Optional vendor layer for the request mapper (spec 01 section 13). Selected only by config.</summary>
public enum AnthropicChatCompatProfile
{
    None = 0,
    DeepSeek = 1,
    MiniMax = 2,
    Mimo = 3,
    Zhipu = 4
}

public static class AnthropicChatCompatProfileParser
{
    public static bool TryParse(string? value, out AnthropicChatCompatProfile profile)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            profile = AnthropicChatCompatProfile.None;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
                profile = AnthropicChatCompatProfile.None;
                return true;
            case "deepseek":
            case "deep_seek":
                profile = AnthropicChatCompatProfile.DeepSeek;
                return true;
            case "minimax":
            case "mini_max":
                profile = AnthropicChatCompatProfile.MiniMax;
                return true;
            case "mimo":
                profile = AnthropicChatCompatProfile.Mimo;
                return true;
            case "zhipu":
                profile = AnthropicChatCompatProfile.Zhipu;
                return true;
            default:
                profile = AnthropicChatCompatProfile.None;
                return false;
        }
    }

    public static AnthropicChatCompatProfile Parse(string? value) =>
        TryParse(value, out AnthropicChatCompatProfile profile)
            ? profile
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Anthropic chat compat profile.");
}

/// <summary>How <c>metadata.user_id</c> becomes the Chat <c>user</c> field (spec 01 section 10).</summary>
public enum AnthropicChatUserIdMode
{
    Off = 0,
    Hash = 1,
    Passthrough = 2
}

public static class AnthropicChatUserIdModeParser
{
    public static bool TryParse(string? value, out AnthropicChatUserIdMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = AnthropicChatUserIdMode.Off;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
            case "none":
                mode = AnthropicChatUserIdMode.Off;
                return true;
            case "hash":
            case "hashed":
                mode = AnthropicChatUserIdMode.Hash;
                return true;
            case "passthrough":
            case "pass_through":
                mode = AnthropicChatUserIdMode.Passthrough;
                return true;
            default:
                mode = AnthropicChatUserIdMode.Off;
                return false;
        }
    }

    public static AnthropicChatUserIdMode Parse(string? value) =>
        TryParse(value, out AnthropicChatUserIdMode mode)
            ? mode
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Anthropic chat user-id mode.");
}

/// <summary>Name of the Chat output-limit key (spec 01 section 10).</summary>
public enum AnthropicChatMaxTokensField
{
    MaxTokens = 0,
    MaxCompletionTokens = 1
}

public static class AnthropicChatMaxTokensFieldParser
{
    public static bool TryParse(string? value, out AnthropicChatMaxTokensField field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            field = AnthropicChatMaxTokensField.MaxTokens;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "max_tokens":
            case "max-tokens":
                field = AnthropicChatMaxTokensField.MaxTokens;
                return true;
            case "max_completion_tokens":
            case "max-completion-tokens":
                field = AnthropicChatMaxTokensField.MaxCompletionTokens;
                return true;
            default:
                field = AnthropicChatMaxTokensField.MaxTokens;
                return false;
        }
    }

    public static AnthropicChatMaxTokensField Parse(string? value) =>
        TryParse(value, out AnthropicChatMaxTokensField field)
            ? field
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Anthropic chat max-tokens field.");
}

/// <summary>How an unsupported <c>document</c> block is mapped (spec 01 section 7.2).</summary>
public enum AnthropicChatDocumentMode
{
    Placeholder = 0,
    Image = 1
}

public static class AnthropicChatDocumentModeParser
{
    public static bool TryParse(string? value, out AnthropicChatDocumentMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = AnthropicChatDocumentMode.Placeholder;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "placeholder":
                mode = AnthropicChatDocumentMode.Placeholder;
                return true;
            case "image":
                mode = AnthropicChatDocumentMode.Image;
                return true;
            default:
                mode = AnthropicChatDocumentMode.Placeholder;
                return false;
        }
    }

    public static AnthropicChatDocumentMode Parse(string? value) =>
        TryParse(value, out AnthropicChatDocumentMode mode)
            ? mode
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Anthropic chat document mode.");
}

/// <summary>How an in-sequence <c>system</c> message is mapped (spec 01 section 7.0).</summary>
public enum AnthropicChatMidturnSystem
{
    System = 0,
    MergeIntoUser = 1
}

public static class AnthropicChatMidturnSystemParser
{
    public static bool TryParse(string? value, out AnthropicChatMidturnSystem mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = AnthropicChatMidturnSystem.System;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "system":
                mode = AnthropicChatMidturnSystem.System;
                return true;
            case "merge_into_user":
            case "merge-into-user":
                mode = AnthropicChatMidturnSystem.MergeIntoUser;
                return true;
            default:
                mode = AnthropicChatMidturnSystem.System;
                return false;
        }
    }

    public static AnthropicChatMidturnSystem Parse(string? value) =>
        TryParse(value, out AnthropicChatMidturnSystem mode)
            ? mode
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Anthropic chat mid-turn system mode.");
}
