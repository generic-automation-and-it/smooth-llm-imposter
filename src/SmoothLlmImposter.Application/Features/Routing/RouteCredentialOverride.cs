using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Application.Features.Routing;

public sealed record RouteCredentialOverride(
    string Secret,
    CredentialAuthScheme AuthScheme,
    Uri? BaseUrlOverride,
    string? AnthropicVersion);
