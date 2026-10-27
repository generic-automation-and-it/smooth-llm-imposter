using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.ProviderConfiguration;

public sealed record ProviderConfigurationResponse(
    string Key,
    string? Name,
    string Dialect,
    string BaseUrl,
    string? AuthScheme,
    string? AuthHeader,
    bool IsDefault,
    bool Enabled,
    string? AnthropicVersion,
    string? OpenAiUpstreamApi,
    string? RequestNormalization,
    string? SessionForwarding,
    bool? StripEncryptedContent,
    int? TimeoutSeconds,
    IReadOnlyList<ProviderModelMappingResponse> Models,
    string? AnthropicUpstreamApi = null,
    string? CompatProfile = null,
    bool? SendReasoningEffort = null,
    string? UserIdMode = null,
    string? MaxTokensField = null,
    int? MaxOutputTokens = null,
    bool? ForwardCacheControl = null,
    bool? SendResponseFormat = null,
    string? DocumentMode = null,
    bool? ThinkMarkupExtraction = null,
    string? MidturnSystem = null,
    bool? OptimizeMessages = null)
{
    internal static ProviderConfigurationResponse From(string key, ProviderOptions provider) => new(
        key,
        provider.Name,
        provider.Dialect,
        provider.BaseUrl,
        provider.AuthScheme,
        provider.AuthHeader,
        provider.IsDefault,
        provider.Enabled,
        provider.AnthropicVersion,
        provider.OpenAiUpstreamApi,
        provider.RequestNormalization,
        provider.SessionForwarding,
        provider.StripEncryptedContent,
        provider.TimeoutSeconds,
        provider.Models.Select(ProviderModelMappingResponse.FromOptions).ToArray(),
        provider.AnthropicUpstreamApi,
        provider.CompatProfile,
        provider.SendReasoningEffort,
        provider.UserIdMode,
        provider.MaxTokensField,
        provider.MaxOutputTokens,
        provider.ForwardCacheControl,
        provider.SendResponseFormat,
        provider.DocumentMode,
        provider.ThinkMarkupExtraction,
        provider.MidturnSystem,
        provider.OptimizeMessages);
}

public sealed record ProviderConfigurationBody(
    string? Name,
    string Dialect,
    string BaseUrl,
    string? AuthScheme,
    string? AuthHeader,
    bool IsDefault,
    bool Enabled,
    string? AnthropicVersion,
    string? OpenAiUpstreamApi,
    string? RequestNormalization,
    string? SessionForwarding,
    bool? StripEncryptedContent,
    int? TimeoutSeconds,
    IReadOnlyList<ProviderModelMappingBody>? Models,
    string? AnthropicUpstreamApi = null,
    string? CompatProfile = null,
    bool? SendReasoningEffort = null,
    string? UserIdMode = null,
    string? MaxTokensField = null,
    int? MaxOutputTokens = null,
    bool? ForwardCacheControl = null,
    bool? SendResponseFormat = null,
    string? DocumentMode = null,
    bool? ThinkMarkupExtraction = null,
    string? MidturnSystem = null,
    bool? OptimizeMessages = null)
{
    internal ProviderOptions ToProviderOptions(string? secret = null) => new()
    {
        Name = Name,
        Dialect = Dialect,
        BaseUrl = BaseUrl,
        Secret = secret,
        AuthScheme = AuthScheme,
        AuthHeader = AuthHeader,
        IsDefault = IsDefault,
        Enabled = Enabled,
        AnthropicVersion = AnthropicVersion,
        OpenAiUpstreamApi = OpenAiUpstreamApi,
        RequestNormalization = RequestNormalization,
        SessionForwarding = SessionForwarding,
        StripEncryptedContent = StripEncryptedContent,
        TimeoutSeconds = TimeoutSeconds,
        AnthropicUpstreamApi = AnthropicUpstreamApi,
        CompatProfile = CompatProfile,
        SendReasoningEffort = SendReasoningEffort,
        UserIdMode = UserIdMode,
        MaxTokensField = MaxTokensField,
        MaxOutputTokens = MaxOutputTokens,
        ForwardCacheControl = ForwardCacheControl,
        SendResponseFormat = SendResponseFormat,
        DocumentMode = DocumentMode,
        ThinkMarkupExtraction = ThinkMarkupExtraction,
        MidturnSystem = MidturnSystem,
        OptimizeMessages = OptimizeMessages,
        Models = Models?.Select(static model => new ModelMappingOptions
        {
            From = model.From,
            To = model.To,
            Caching = model.Caching
        }).ToList() ?? []
    };
}

public sealed record ProviderModelMappingResponse(string From, string To, bool Caching)
{
    internal static ProviderModelMappingResponse FromOptions(ModelMappingOptions mapping) =>
        new(mapping.From, mapping.To, mapping.Caching);
}

public sealed record ProviderModelMappingBody(string From, string To, bool Caching);
