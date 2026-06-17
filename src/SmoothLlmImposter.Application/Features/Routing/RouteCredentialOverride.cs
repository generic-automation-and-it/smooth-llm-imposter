using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// The decrypted credential the forwarder applies on the passthrough path. <see cref="ForceBearer"/> is set
/// when the dialect's authorization override is ON (HLD 003): the forwarder then presents the secret as
/// <c>Authorization: Bearer</c> and omits <c>x-api-key</c>, regardless of <see cref="AuthScheme"/>.
/// </summary>
public sealed record RouteCredentialOverride(
    string Secret,
    CredentialAuthScheme AuthScheme,
    Uri? BaseUrlOverride,
    string? AnthropicVersion,
    bool ForceBearer = false);
