namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Root configuration bound from the <c>Imposter</c> section. Lives only in configuration —
/// secrets are never persisted. Providers are keyed by <b>name</b> (never index): <c>appsettings.json</c>
/// values are overridden by structured environment variables (e.g.
/// <c>Imposter__Providers__opencode-go__Secret</c>) and by the conventional per-provider surface
/// (<c>OPENCODE_GO_API_KEY</c>), the latter taking precedence (see <see cref="ImposterOptionsPostConfigure"/>).
/// </summary>
public sealed class ImposterOptions
{
    public const string SectionName = "Imposter";

    /// <summary>
    /// Providers keyed by name — the key is the provider's stable identity, so overrides addressed by
    /// name survive any reordering (HLD 007). The default (ordinal) comparer is intentional: it keeps
    /// case-only-duplicate keys distinct so the validator can reject them, rather than the binder
    /// throwing on collision. A legacy JSON array binds here as numeric keys (<c>"0"</c>, <c>"1"</c>, …)
    /// — the validator rejects that shape.
    /// </summary>
    public Dictionary<string, ProviderOptions> Providers { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>One upstream: a base URL + secret + dialect. Identity is the dictionary key under which it
/// is declared; <see cref="Name"/> is an optional display override of that key.</summary>
public sealed class ProviderOptions
{
    /// <summary>
    /// Optional display-name override. When omitted (<c>null</c>) the provider takes its name from its
    /// dictionary key; a set value wins. A blank/whitespace value is rejected by the validator (omit it
    /// instead of blanking it). Not part of the conventional env surface — the key is the identity.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Wire dialect: <c>openai</c> or <c>anthropic</c>.</summary>
    public string Dialect { get; set; } = string.Empty;

    /// <summary>Server root WITHOUT the version path, e.g. <c>https://api.openai.com</c>. The inbound request path is appended.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Upstream credential. Sourced from config/env only, never stored.</summary>
    public string? Secret { get; set; }

    /// <summary>
    /// How <see cref="Secret"/> is presented upstream, independent of <see cref="Dialect"/>:
    /// <c>Bearer</c> → <c>Authorization: Bearer</c>, <c>ApiKey</c> → <c>x-api-key</c>. When omitted,
    /// the dialect default applies (openai → Bearer, anthropic → ApiKey).
    /// </summary>
    public string? AuthScheme { get; set; }

    /// <summary>
    /// Optional override of the <b>header name</b> the credential is written into. When omitted (<c>null</c>)
    /// the scheme's default header is used (<c>Bearer</c> → <c>Authorization</c>, <c>ApiKey</c> →
    /// <c>x-api-key</c>). The value format still follows <see cref="AuthScheme"/> — a <c>Bearer</c> provider
    /// keeps the <c>Bearer </c> prefix, an <c>ApiKey</c> provider stays the raw token. Set it for a gateway
    /// that expects a non-standard header, e.g. the LEGO codex gateway's <c>api-key</c>
    /// (<c>AuthScheme=ApiKey</c> + <c>AuthHeader=api-key</c> → <c>api-key: &lt;token&gt;</c>). A
    /// present-but-blank value is rejected by the validator (omit it instead).
    /// </summary>
    public string? AuthHeader { get; set; }

    /// <summary>When no model mapping matches, the dialect's default provider receives the request unchanged.</summary>
    public bool IsDefault { get; set; }

    /// <summary>When false, the provider remains configured but is excluded from all routing resolution.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional override for the <c>anthropic-version</c> header (anthropic dialect only).</summary>
    public string? AnthropicVersion { get; set; }

    /// <summary>
    /// OpenAI-dialect upstream API surface. Defaults to <c>responses</c>; set to
    /// <c>chat_completions</c> for OpenAI-compatible providers that do not expose <c>/responses</c>.
    /// </summary>
    public string? OpenAiUpstreamApi { get; set; }

    /// <summary>
    /// Proxy-side request-normalization profile (HLD 004), off by default. Set to
    /// <c>codex_to_openai_sdk</c> for strict OpenAI-compatible upstreams that reject Codex's full tool
    /// catalog. Applies only on matched OpenAI imposter routes; passthrough/default routes stay
    /// byte-transparent regardless of this value.
    /// </summary>
    public string? RequestNormalization { get; set; }

    /// <summary>From→to model mappings owned by this provider. Structured-only — not part of the
    /// conventional env surface (HLD 007 LADR-02).</summary>
    public List<ModelMappingOptions> Models { get; init; } = [];
}

/// <summary>A from→to model rewrite with an optional caching opt-in.</summary>
public sealed class ModelMappingOptions
{
    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public bool Caching { get; init; }
}
