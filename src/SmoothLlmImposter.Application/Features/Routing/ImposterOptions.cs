using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;

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

    /// <summary>
    /// In-band routing probe (HLD 010). When enabled, a request whose last user message is the exact
    /// string <c>who?</c> (trimmed, case-sensitive, non-streaming) short-circuits the forward path and
    /// returns a dialect-shaped synthetic reply describing the resolved route and auth scheme.
    /// Default <c>true</c>; conventional env override <c>IMPOSTER_WHO_MESSAGE_ENABLED</c>.
    /// </summary>
    public WhoMessageOptions WhoMessage { get; init; } = new();

    /// <summary>
    /// Shared decision-model defaults (HLD 017 LADR-06, HLD 015 LADR-05): every value fills a decision
    /// section field the section itself leaves unset. Unset here means the section keeps its built-in default.
    /// </summary>
    public DecisionsOptions Decisions { get; init; } = new();

    public DecisionsToolsOptions DecisionsTools { get; init; } = new();

    /// <summary>
    /// Message optimization as a decision-service consumer (HLD 017). Its own enable flag gates the
    /// message-optimization stage. Its transport fields are the message gate's own decision profile
    /// (HLD 015 LADR-05); they never inherit the tool section's values.
    /// </summary>
    public DecisionsMessagesOptions DecisionsMessages { get; init; } = new();

    public LlmMessagesOptions LlmMessages { get; init; } = new();

    public ToolOrderingOptions ToolOrdering { get; init; } = new();

    /// <summary>
    /// Maximum number of upstream and GitHub HTTP requests that may be in flight at once across the whole
    /// process (HLD 017 follow-up). <c>1</c> (default) serializes them so a slow backend cannot be hit
    /// concurrently; higher values allow overlap. Conventional env override
    /// <c>IMPOSTER_MAX_CONCURRENT_REQUESTS</c>.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1;
}

/// <summary>
/// The shared <c>DECISIONS_*</c> layer. Every property is nullable because only a set value may fill a
/// section; <c>MinKeepTools</c>, <c>AlwaysKeep</c> and <c>StickyPerSession</c> exist only on the tool section,
/// so they default that section alone. There is deliberately no <c>Enabled</c>: one variable must never
/// switch on two features.
/// </summary>
public sealed class DecisionsOptions
{
    public string? BaseUrl { get; set; }

    public string? Path { get; set; }

    public string? Model { get; set; }

    public double? MinProbability { get; set; }

    public int? MaxAttempts { get; set; }

    public int? TimeoutSeconds { get; set; }

    public int? RetryDelaySeconds { get; set; }

    public int? CircuitBreakSeconds { get; set; }

    public int? MinKeepTools { get; set; }

    public string? AlwaysKeep { get; set; }

    public bool? StickyPerSession { get; set; }
}

public sealed class DecisionsMessagesOptions
{
    /// <summary>
    /// Gates the message-optimization decision stage. When unset, takes the value of
    /// <c>LLM_MESSAGES_ENABLED</c> (LADR-06); defaults to <c>false</c>.
    /// </summary>
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = DecisionServiceOptions.DefaultBaseUrl;

    public string Path { get; set; } = DecisionServiceOptions.DefaultPath;

    public string Model { get; set; } = DecisionServiceOptions.DefaultModel;

    public int MaxAttempts { get; set; } = DecisionServiceOptions.DefaultMaxAttempts;

    public int TimeoutSeconds { get; set; } = DecisionServiceOptions.DefaultTimeoutSeconds;

    public int RetryDelaySeconds { get; set; } = DecisionServiceOptions.DefaultRetryDelaySeconds;

    /// <summary>
    /// Minimum probability (inclusive) for a positive "can be optimized" answer.
    /// </summary>
    public double MinProbability { get; set; } = 0.50;

    /// <summary>
    /// Cooldown for the message-gate circuit (HLD 017 LADR-07), in seconds.
    /// </summary>
    public int CircuitBreakSeconds { get; set; } = DecisionsToolsOptions.DefaultCircuitBreakSeconds;

    /// <summary>
    /// Maximum size (in chars) of the combined human text sent to the message classifier gate.
    /// Messages whose combined human text exceeds this are not classified (the gate is bypassed
    /// and optimization proceeds directly), because the classifier model cannot handle the payload
    /// and would only return HTTP 413. The optimizer still receives the full, untruncated text.
    /// Default 6000.
    /// </summary>
    public int MaxStateChars { get; set; } = 6000;
}

public sealed class ToolOrderingOptions
{
    public bool Enabled { get; set; }
}

public sealed class DecisionsToolsOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = DecisionServiceOptions.DefaultBaseUrl;

    public string Path { get; set; } = DecisionServiceOptions.DefaultPath;

    public string Model { get; set; } = DecisionServiceOptions.DefaultModel;

    public double MinProbability { get; set; } = 0.50;

    public int MinKeepTools { get; set; } = 20;

    public string AlwaysKeep { get; set; } = "Bash,Read,Edit,Write,Skill";

    public bool StickyPerSession { get; set; }

    public int MaxAttempts { get; set; } = DecisionServiceOptions.DefaultMaxAttempts;

    public int TimeoutSeconds { get; set; } = DecisionServiceOptions.DefaultTimeoutSeconds;

    public int RetryDelaySeconds { get; set; } = DecisionServiceOptions.DefaultRetryDelaySeconds;

    /// <summary>
    /// Cooldown after one failed classifier call opens the tool-filter circuit (HLD 014 LADR-11, NFR-05).
    /// While open, passes skip classification and forward the catalog unchanged.
    /// </summary>
    public int CircuitBreakSeconds { get; set; } = DefaultCircuitBreakSeconds;

    public const int DefaultCircuitBreakSeconds = 180;
}

/// <summary>
/// In-band routing-probe config (HLD 010). Boolean-only today; reserved for future probe tunables
/// (trigger shape, response verbosity) without churning <see cref="ImposterOptions"/>.
/// </summary>
public sealed class WhoMessageOptions
{
    /// <summary>
    /// When true, the proxy intercepts a request whose last user message is <c>who?</c> and answers
    /// locally with a dialect-shaped synthetic reply. When false, the forward path is byte-identical
    /// to the pre-HLD-010 router (no body parsing for this concern).
    /// </summary>
    public bool Enabled { get; set; } = true;
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
    /// that expects a non-standard header, e.g. the MyCompany Gateway's <c>api-key</c>
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

    /// <summary>
    /// Proxy-side session-identity forwarding profile (HLD 009), off by default. Set to
    /// <c>opencode-go</c> so a matched imposter route stamps a resolved session identity
    /// (<c>session_id</c> body field on OpenAI + <c>x-opencode-session</c> header) for upstreams
    /// that group traffic by session. Passthrough/default routes stay byte-transparent regardless.
    /// </summary>
    public string? SessionForwarding { get; set; }

    /// <summary>
    /// Proxy-side ZDR sanitation (HLD 011), off by default. When <c>true</c>, the forward path drops
    /// <c>reasoning</c> input items that carry OpenAI server-side-encrypted <c>encrypted_content</c>
    /// before forwarding, so a <c>/responses</c> upstream that cannot decrypt (e.g. LM Studio) does not
    /// reject the request with "Encrypted content is not supported." Independent of
    /// <see cref="OpenAiUpstreamApi"/> — it applies on both the <c>responses</c> and, composed before the
    /// downgrade, the <c>chat_completions</c> forward path.
    /// </summary>
    public bool? StripEncryptedContent { get; set; }

    /// <summary>
    /// Per-route opt-out for the HLD 017 message optimizer. <c>null</c> (default) and <c>true</c> both
    /// leave message optimization on; only <c>false</c> skips it for this provider's routes. Use it where the
    /// gate and optimizer latency outweighs the token saving, or for an upstream still billed per request. The tool-filter strategy is unaffected. Conventional env
    /// <c>&lt;PROVIDER&gt;_OPTIMIZE_MESSAGES</c>.
    /// </summary>
    public bool? OptimizeMessages { get; set; }

    /// <summary>
    /// Per-provider base upstream header timeout in seconds (HLD 012). <c>null</c> (default) uses the
    /// global 300 s base. The forwarder relays it to the resilience handler, which scales it across the
    /// three attempts — base ×1, ×2, ×3 (default 300/600/900 s) — so a slow upstream can be given more
    /// head-room without changing the ladder shape. It bounds only the wait for <b>response headers</b>
    /// (the client sends with <c>ResponseHeadersRead</c>); once headers arrive an SSE body streams for as
    /// long as the caller stays connected. Validator range: 1–3600. Conventional env
    /// <c>&lt;PROVIDER&gt;_TIMEOUT_SECONDS</c>.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Anthropic-dialect upstream API surface (HLD 016). Defaults to <c>messages</c>; set to
    /// <c>chat_completions</c> to opt a matched imposter route into the Messages-to-Chat bridge.
    /// Only valid on the <c>anthropic</c> dialect, and requires the effective auth scheme to be <c>Bearer</c>.
    /// </summary>
    public string? AnthropicUpstreamApi { get; set; }

    /// <summary>Optional vendor compatibility profile for the bridge (HLD 016): none, deepseek, minimax, mimo or zhipu.</summary>
    public string? CompatProfile { get; set; }

    /// <summary>Opt-in per provider for sending Chat reasoning controls (HLD 016). Default false.</summary>
    public bool? SendReasoningEffort { get; set; }

    /// <summary>How <c>metadata.user_id</c> becomes the Chat <c>user</c> field (HLD 016): off, hash or passthrough.</summary>
    public string? UserIdMode { get; set; }

    /// <summary>Name of the Chat output-limit key (HLD 016): max_tokens or max_completion_tokens.</summary>
    public string? MaxTokensField { get; set; }

    /// <summary>Optional upper cap applied to the bridged output limit (HLD 016), integer &gt;= 16.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Copies Anthropic-style <c>cache_control</c> objects onto Chat parts (HLD 016). Default false.</summary>
    public bool? ForwardCacheControl { get; set; }

    /// <summary>Maps <c>output_config.format</c> to Chat <c>response_format</c> (HLD 016). Default false.</summary>
    public bool? SendResponseFormat { get; set; }

    /// <summary>How an unsupported document block is bridged (HLD 016): placeholder or image.</summary>
    public string? DocumentMode { get; set; }

    /// <summary>Moves a leading think-markup span into reasoning text (HLD 016). Default false.</summary>
    public bool? ThinkMarkupExtraction { get; set; }

    /// <summary>How an in-sequence system message is bridged (HLD 016): system or merge_into_user.</summary>
    public string? MidturnSystem { get; set; }

    /// <summary>From→to model mappings owned by this provider. Structured-only — not part of the
    /// conventional env surface (HLD 007 LADR-02).</summary>
    public List<ModelMappingOptions> Models { get; init; } = [];
}

/// <summary>A from→to model rewrite with an optional caching opt-in.</summary>
public sealed class ModelMappingOptions
{
    // Mutable setters (not init): the configuration binding source generator emits assignments
    // only for settable properties, so init-only leaves bind on JIT (reflection) but silently
    // stay default on NativeAOT. Matches every other bindable property in this file.
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public bool Caching { get; set; }
}
