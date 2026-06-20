namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// A configured upstream: one base URL, one key, one dialect, identified by <see cref="Name"/>.
/// Holds the model mappings that select it. When no mapping matches an inbound model, the
/// dialect's <see cref="IsDefault"/> provider receives the request unchanged (real-provider passthrough).
/// </summary>
public sealed record ProviderRoute(
    string Name,
    ApiDialect Dialect,
    Uri BaseUrl,
    string? ApiKey,
    bool IsDefault,
    string? AnthropicVersion,
    IReadOnlyList<ModelMapping> Models,
    OpenAiUpstreamApi OpenAiUpstreamApi = OpenAiUpstreamApi.Responses);
