namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Root configuration bound from the <c>Imposter</c> section. Lives only in configuration —
/// keys are never persisted. <c>appsettings.json</c> values are overridden by environment
/// variables (e.g. <c>Imposter__Providers__0__ApiKey</c>), which take precedence.
/// </summary>
public sealed class ImposterOptions
{
    public const string SectionName = "Imposter";

    public List<ProviderOptions> Providers { get; init; } = [];
}

/// <summary>One upstream: a base URL + key + dialect, identified by <see cref="Name"/>.</summary>
public sealed class ProviderOptions
{
    /// <summary>Operator-facing provider name (unique). Used for diagnostics and route selection.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Wire dialect: <c>openai</c> or <c>anthropic</c>.</summary>
    public string Dialect { get; init; } = string.Empty;

    /// <summary>Server root WITHOUT the version path, e.g. <c>https://api.openai.com</c>. The inbound request path is appended.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Upstream credential. Sourced from config/env only, never stored.</summary>
    public string? ApiKey { get; init; }

    /// <summary>When no model mapping matches, the dialect's default provider receives the request unchanged.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Optional override for the <c>anthropic-version</c> header (anthropic dialect only).</summary>
    public string? AnthropicVersion { get; init; }

    /// <summary>
    /// OpenAI-dialect upstream API surface. Defaults to <c>responses</c>; set to
    /// <c>chat_completions</c> for OpenAI-compatible providers that do not expose <c>/responses</c>.
    /// </summary>
    public string? OpenAiUpstreamApi { get; init; }

    /// <summary>From→to model mappings owned by this provider.</summary>
    public List<ModelMappingOptions> Models { get; init; } = [];
}

/// <summary>A from→to model rewrite with an optional caching opt-in.</summary>
public sealed class ModelMappingOptions
{
    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public bool Caching { get; init; }
}
