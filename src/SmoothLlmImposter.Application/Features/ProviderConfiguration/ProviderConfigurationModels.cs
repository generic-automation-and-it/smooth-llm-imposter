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
    IReadOnlyList<ProviderModelMappingResponse> Models)
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
        provider.Models.Select(ProviderModelMappingResponse.FromOptions).ToArray());
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
    IReadOnlyList<ProviderModelMappingBody>? Models)
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
