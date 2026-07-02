namespace SmoothLlmImposter.Application.Features.Routing;

internal static class ProviderOptionsCloner
{
    public static ProviderOptions Clone(ProviderOptions source) => new()
    {
        Name = source.Name,
        Dialect = source.Dialect,
        BaseUrl = source.BaseUrl,
        Secret = source.Secret,
        AuthScheme = source.AuthScheme,
        AuthHeader = source.AuthHeader,
        IsDefault = source.IsDefault,
        Enabled = source.Enabled,
        AnthropicVersion = source.AnthropicVersion,
        OpenAiUpstreamApi = source.OpenAiUpstreamApi,
        RequestNormalization = source.RequestNormalization,
        Models = [.. source.Models.Select(Clone)]
    };

    public static ModelMappingOptions Clone(ModelMappingOptions source) => new()
    {
        From = source.From,
        To = source.To,
        Caching = source.Caching
    };

    public static Dictionary<string, ProviderOptions> CloneDictionary(IReadOnlyDictionary<string, ProviderOptions> source)
    {
        var clone = new Dictionary<string, ProviderOptions>(StringComparer.Ordinal);
        foreach ((string key, ProviderOptions provider) in source)
        {
            clone[key] = Clone(provider);
        }

        return clone;
    }
}
