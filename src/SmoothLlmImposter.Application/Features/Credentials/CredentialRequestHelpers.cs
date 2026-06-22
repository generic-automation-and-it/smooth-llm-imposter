using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Credentials;

internal static class CredentialRequestHelpers
{
    public static ApiDialect ParseDialect(string dialect)
    {
        if (ApiDialectParser.TryParse(dialect, out ApiDialect parsed))
        {
            return parsed;
        }

        throw new ArgumentException("Provider dialect must be 'openai' or 'anthropic'.", nameof(dialect));
    }

    public static CredentialAuthScheme ParseAuthScheme(string authScheme)
    {
        if (Enum.TryParse(authScheme, ignoreCase: true, out CredentialAuthScheme parsed))
        {
            return parsed;
        }

        throw new ArgumentException("Auth scheme must be 'ApiKey' or 'Bearer'.", nameof(authScheme));
    }

    public static ProviderCredential NewCredential(
        ApiDialect dialect,
        string providerName,
        string name,
        string secretCiphertext,
        CredentialAuthScheme authScheme,
        string? baseUrlOverride,
        string? anthropicVersion) => dialect switch
    {
        ApiDialect.OpenAi => new OpenAiCredential(providerName, name, secretCiphertext, authScheme, baseUrlOverride),
        ApiDialect.Anthropic => new AnthropicCredential(providerName, name, secretCiphertext, authScheme, baseUrlOverride, anthropicVersion),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported provider dialect.")
    };
}
