using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Application.Features.Credentials;

public sealed record CredentialResponse(
    Guid Id,
    string ProviderDialect,
    string Name,
    CredentialAuthScheme AuthScheme,
    bool IsActive,
    string? BaseUrlOverride,
    string? AnthropicVersion,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static CredentialResponse From(ProviderCredential credential) => new(
        credential.Id,
        credential.ProviderDialect,
        credential.Name,
        credential.AuthScheme,
        credential.IsActive,
        credential.BaseUrlOverride,
        credential is AnthropicCredential anthropic ? anthropic.AnthropicVersion : null,
        credential.CreatedAtUtc,
        credential.UpdatedAtUtc);
}

public sealed record CredentialMutationResponse(CredentialResponse Credential);
